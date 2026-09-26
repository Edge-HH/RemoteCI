using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>班级管理：增删改查、每班访客开关、成员管理与系统管理员批量操作。</summary>
public sealed class ClassroomService(AppDbContext db)
{
    public async Task<IReadOnlyList<ClassDetail>> ListAsync(CancellationToken ct = default)
    {
        // SQLite 不支持 DateTimeOffset 排序的 SQL 翻译；班级数量极少，取回后在内存排序。
        var classrooms = (await db.Classrooms.AsNoTracking().ToListAsync(ct)).OrderBy(x => x.CreatedAt).ToList();
        var memberCounts = await db.ClassMemberships.AsNoTracking()
            .GroupBy(x => x.ClassroomId)
            .Select(x => new { ClassroomId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.ClassroomId, x => x.Count, ct);
        // 插件在线数来自连接注册表；服务是 scoped，通过注入的在线快照函数避免引用单例链。
        return classrooms.Select(x => new ClassDetail
        {
            Id = x.Id,
            Name = x.Name,
            VisitorEnabled = x.VisitorAccessEnabled,
            MemberCount = memberCounts.GetValueOrDefault(x.Id),
            PluginCount = OnlinePluginCounter?.Invoke(x.Id) ?? 0,
            CreatedAt = x.CreatedAt,
        }).ToList();
    }

    /// <summary>由 DI 注入的在线插件统计回调（PeerRegistry 是单例，服务是 scoped）。</summary>
    public Func<Guid, int>? OnlinePluginCounter { get; set; }

    public async Task<Classroom> RequireAsync(Guid id, CancellationToken ct = default) =>
        await db.Classrooms.SingleOrDefaultAsync(x => x.Id == id, ct)
        ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "班级不存在");

    public async Task<ClassDetail> CreateAsync(string name, CancellationToken ct = default)
    {
        var trimmed = ValidateName(name);
        if (await db.Classrooms.AnyAsync(x => x.Name == trimmed, ct))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "班级名称已存在");
        var now = DateTimeOffset.UtcNow;
        var classroom = new Classroom { Id = Guid.NewGuid(), Name = trimmed, CreatedAt = now, UpdatedAt = now };
        db.Classrooms.Add(classroom);
        await db.SaveChangesAsync(ct);
        return new ClassDetail { Id = classroom.Id, Name = classroom.Name, CreatedAt = classroom.CreatedAt };
    }

    public async Task RenameAsync(Guid id, string name, CancellationToken ct = default)
    {
        var classroom = await RequireAsync(id, ct);
        var trimmed = ValidateName(name);
        if (await db.Classrooms.AnyAsync(x => x.Name == trimmed && x.Id != id, ct))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "班级名称已存在");
        classroom.Name = trimmed;
        classroom.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetVisitorAccessAsync(Guid id, bool enabled, CancellationToken ct = default)
    {
        var classroom = await RequireAsync(id, ct);
        if (classroom.VisitorAccessEnabled == enabled) return;
        classroom.VisitorAccessEnabled = enabled;
        classroom.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>删除班级并级联清理成员关系与插件凭据（凭据需重新配对）；默认班级不可删除。</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (id == Classroom.DefaultId)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "默认班级不能删除");
        var classroom = await RequireAsync(id, ct);
        db.Classrooms.Remove(classroom);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ClassMemberInfo>> ListMembersAsync(Guid id, CancellationToken ct = default)
    {
        await RequireAsync(id, ct);
        var members = await db.ClassMemberships.AsNoTracking()
            .Where(x => x.ClassroomId == id)
            .Join(db.Users.AsNoTracking(), x => x.UserId, y => y.Id, (x, y) => new { x.RoleDefinitionId, y.Id, y.UserName, y.DisplayName })
            .Join(db.AccountRoles.AsNoTracking(), x => x.RoleDefinitionId, y => y.Id,
                (x, y) => new ClassMemberInfo
                {
                    UserId = x.Id,
                    Username = x.UserName ?? string.Empty,
                    DisplayName = x.DisplayName,
                    RoleId = y.Id,
                    RoleName = y.Name,
                })
            .OrderBy(x => x.Username)
            .ToListAsync(ct);
        return members;
    }

    /// <summary>整体替换班级成员列表；请求中引用的用户与角色必须存在。</summary>
    public async Task UpdateMembersAsync(Guid id, UpdateClassMembersRequest request, CancellationToken ct = default)
    {
        await RequireAsync(id, ct);
        var seenUsers = new HashSet<Guid>();
        foreach (var member in request.Members)
        {
            if (!seenUsers.Add(member.UserId))
                throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "成员列表中存在重复用户");
        }
        var userIds = request.Members.Select(x => x.UserId).ToList();
        var roleIds = request.Members.Select(x => x.RoleId).ToList();
        var foundUsers = await db.Users.Where(x => userIds.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
        if (foundUsers.Count != seenUsers.Count)
            throw new IdentityOperationException(ApiErrorCodes.NotFound, "成员中包含不存在的用户");
        var roleCount = await db.AccountRoles.Where(x => roleIds.Distinct().Contains(x.Id)).CountAsync(ct);
        if (roleCount != roleIds.Distinct().Count())
            throw new IdentityOperationException(ApiErrorCodes.NotFound, "成员角色中包含不存在的角色");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.ClassMemberships.Where(x => x.ClassroomId == id).ExecuteDeleteAsync(ct);
        db.ClassMemberships.AddRange(request.Members.Select(x => new ClassMembership
        {
            UserId = x.UserId,
            ClassroomId = id,
            RoleDefinitionId = x.RoleId,
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>批量操作：返回每个班级的独立结果，单个失败不影响其余班级。</summary>
    public async Task<BatchClassOperationResult> BatchAsync(
        BatchClassOperationRequest request, CancellationToken ct = default)
    {
        var results = new List<BatchClassItemResult>();
        foreach (var classId in request.ClassIds.Distinct())
        {
            try
            {
                switch (request.Operation.Trim().ToLowerInvariant())
                {
                    case "enablevisitor":
                        await SetVisitorAccessAsync(classId, true, ct);
                        break;
                    case "disablevisitor":
                        await SetVisitorAccessAsync(classId, false, ct);
                        break;
                    case "delete":
                        await DeleteAsync(classId, ct);
                        break;
                    default:
                        throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "未知批量操作");
                }
                results.Add(new BatchClassItemResult { ClassId = classId, Success = true });
            }
            catch (IdentityOperationException ex)
            {
                results.Add(new BatchClassItemResult { ClassId = classId, Success = false, Message = ex.Message });
            }
        }
        return new BatchClassOperationResult { Results = results };
    }

    private static string ValidateName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 40)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "班级名称需为 1-40 个字符");
        return trimmed;
    }
}
