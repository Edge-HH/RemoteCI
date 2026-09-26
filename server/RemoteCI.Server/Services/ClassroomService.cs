using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>班级管理：增删改查、每班访客开关、班名/头像、分组（支持层级与多归属）与批量操作。</summary>
public sealed class ClassroomService(AppDbContext db)
{
    public async Task<IReadOnlyList<ClassDetail>> ListAsync(CancellationToken ct = default)
    {
        // SQLite 不支持 DateTimeOffset 排序的 SQL 翻译；班级数量极少，取回后在内存排序。
        var classrooms = (await db.Classrooms
                .Include(x => x.GroupAssignments).ThenInclude(a => a.Group)
                .AsNoTracking().ToListAsync(ct))
            .OrderBy(x => x.CreatedAt).ToList();
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
            GroupIds = x.GroupAssignments.Select(a => a.GroupId).ToList(),
            GroupNames = x.GroupAssignments.Select(a => a.Group.Name).ToList(),
            HasAvatar = x.Avatar != null,
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

    /// <summary>删除班级并级联清理成员关系、分组归属与插件凭据（凭据需重新配对）；默认班级不可删除。</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (id == Classroom.DefaultId)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "默认班级不能删除");
        var classroom = await RequireAsync(id, ct);
        db.Classrooms.Remove(classroom);
        await db.SaveChangesAsync(ct);
    }

    // ---------- 班级信息（班名/头像）：班管理员与系统管理员可用 ----------

    /// <summary>更新班名；调用方负责先做权限校验（系统管理员或本班班管理员）。</summary>
    public Task RenameClassAsync(Guid classId, string name, CancellationToken ct = default) => RenameAsync(classId, name, ct);

    /// <summary>设置或清除班级头像；avatar 为 null 表示清除。</summary>
    public async Task SetAvatarAsync(Guid classId, byte[]? avatar, string? contentType, CancellationToken ct = default)
    {
        var classroom = await RequireAsync(classId, ct);
        classroom.Avatar = avatar;
        classroom.AvatarContentType = avatar is null ? null : contentType;
        classroom.AvatarUpdatedAt = avatar is null ? null : DateTimeOffset.UtcNow;
        classroom.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>按名称查找班级；不存在时自动创建（批量导入用）。</summary>
    public async Task<Classroom> EnsureClassByNameAsync(string name, CancellationToken ct = default)
    {
        var trimmed = ValidateName(name);
        var existing = await db.Classrooms.SingleOrDefaultAsync(x => x.Name == trimmed, ct);
        if (existing is not null) return existing;
        var now = DateTimeOffset.UtcNow;
        var classroom = new Classroom { Id = Guid.NewGuid(), Name = trimmed, CreatedAt = now, UpdatedAt = now };
        db.Classrooms.Add(classroom);
        await db.SaveChangesAsync(ct);
        return classroom;
    }

    // ---------- 成员 ----------

    /// <summary>向班级添加单个成员（幂等）；用户与角色必须存在。</summary>
    public async Task AddMemberAsync(Guid classId, Guid userId, Guid roleId, CancellationToken ct = default)
    {
        await RequireAsync(classId, ct);
        if (await db.ClassMemberships.AnyAsync(x => x.ClassroomId == classId && x.UserId == userId, ct)) return;
        if (!await db.Users.AnyAsync(x => x.Id == userId, ct))
            throw new IdentityOperationException(ApiErrorCodes.NotFound, "用户不存在");
        if (!await db.AccountRoles.AnyAsync(x => x.Id == roleId, ct))
            throw new IdentityOperationException(ApiErrorCodes.NotFound, "角色不存在");
        db.ClassMemberships.Add(new ClassMembership
        {
            UserId = userId,
            ClassroomId = classId,
            RoleDefinitionId = roleId,
        });
        await db.SaveChangesAsync(ct);
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

    // ---------- 班级分组：层级 + 多归属 ----------

    public async Task<IReadOnlyList<ClassGroupInfo>> ListGroupsAsync(CancellationToken ct = default)
    {
        var groups = (await db.ClassGroups.Include(x => x.Parent).AsNoTracking().ToListAsync(ct))
            .OrderBy(x => x.CreatedAt).ToList();
        var counts = await db.ClassGroupAssignments.AsNoTracking()
            .GroupBy(x => x.GroupId)
            .Select(x => new { GroupId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.GroupId, x => x.Count, ct);
        return groups.Select(x => new ClassGroupInfo
        {
            Id = x.Id,
            Name = x.Name,
            ParentId = x.ParentId,
            ParentName = x.Parent?.Name,
            Depth = DepthOf(x, groups),
            ClassCount = counts.GetValueOrDefault(x.Id),
        }).ToList();
    }

    public async Task<ClassGroupInfo> CreateGroupAsync(string name, Guid? parentId, CancellationToken ct = default)
    {
        var trimmed = ValidateGroupName(name);
        if (await db.ClassGroups.AnyAsync(x => x.Name == trimmed, ct))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "分组名称已存在");
        var parent = await RequireGroupIfAnyAsync(parentId, ct);
        if (parent is not null)
        {
            var all = await db.ClassGroups.AsNoTracking().ToListAsync(ct);
            if (DepthOf(parent, all) + 1 > ClassGroup.MaxDepth)
                throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, $"分组最多支持 {ClassGroup.MaxDepth} 层");
        }
        var now = DateTimeOffset.UtcNow;
        var group = new ClassGroup { Id = Guid.NewGuid(), Name = trimmed, ParentId = parent?.Id, CreatedAt = now, UpdatedAt = now };
        db.ClassGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return new ClassGroupInfo { Id = group.Id, Name = group.Name, ParentId = group.ParentId };
    }

    public async Task RenameGroupAsync(Guid id, string name, Guid? parentId, CancellationToken ct = default)
    {
        var group = await RequireGroupAsync(id, ct);
        var trimmed = ValidateGroupName(name);
        if (await db.ClassGroups.AnyAsync(x => x.Name == trimmed && x.Id != id, ct))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "分组名称已存在");

        var parent = await RequireGroupIfAnyAsync(parentId, ct);
        if (parent is not null)
        {
            if (parent.Id == id)
                throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "不能把分组移动到自身之下");
            var all = await db.ClassGroups.AsNoTracking().ToListAsync(ct);
            if (IsDescendant(all, parent.Id, id))
                throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "不能把分组移动到自己的子分组之下");
            if (DepthOf(parent, all) + 1 > ClassGroup.MaxDepth)
                throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, $"分组最多支持 {ClassGroup.MaxDepth} 层");
        }

        group.Name = trimmed;
        group.ParentId = parent?.Id;
        group.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>删除分组：子分组上移到被删分组的父级，班级归属随级联清理；班级本身不受影响。</summary>
    public async Task DeleteGroupAsync(Guid id, CancellationToken ct = default)
    {
        var group = await RequireGroupAsync(id, ct);
        await db.ClassGroups.Where(x => x.ParentId == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ParentId, group.ParentId), ct);
        db.ClassGroups.Remove(group);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>整体替换一个班级所属的分组。</summary>
    public async Task SetClassGroupsAsync(Guid classId, IReadOnlyCollection<Guid> groupIds, CancellationToken ct = default)
    {
        await RequireAsync(classId, ct);
        var wanted = groupIds.Distinct().ToList();
        var found = await db.ClassGroups.Where(x => wanted.Contains(x.Id)).CountAsync(ct);
        if (found != wanted.Count)
            throw new IdentityOperationException(ApiErrorCodes.NotFound, "包含不存在的分组");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.ClassGroupAssignments.Where(x => x.ClassroomId == classId).ExecuteDeleteAsync(ct);
        db.ClassGroupAssignments.AddRange(wanted.Select(groupId => new ClassGroupAssignment
        {
            GroupId = groupId,
            ClassroomId = classId,
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>整体替换一个分组包含的班级（仅直接归属；子分组的班级不受影响）。</summary>
    public async Task SetGroupClassesAsync(Guid groupId, IReadOnlyCollection<Guid> classIds, CancellationToken ct = default)
    {
        await RequireGroupAsync(groupId, ct);
        var wanted = classIds.Distinct().ToList();
        var found = await db.Classrooms.Where(x => wanted.Contains(x.Id)).CountAsync(ct);
        if (found != wanted.Count)
            throw new IdentityOperationException(ApiErrorCodes.NotFound, "包含不存在的班级");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.ClassGroupAssignments.Where(x => x.GroupId == groupId).ExecuteDeleteAsync(ct);
        db.ClassGroupAssignments.AddRange(wanted.Select(classId => new ClassGroupAssignment
        {
            GroupId = groupId,
            ClassroomId = classId,
        }));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>
    /// 展开批量/广播的目标班级：分组递归展开（含全部子分组）后与显式 classIds 合并去重，
    /// 不存在的班级/分组直接以异常呈现而不是静默忽略。
    /// </summary>
    public async Task<List<Guid>> ResolveTargetClassIdsAsync(
        IReadOnlyCollection<Guid> classIds, IReadOnlyCollection<Guid>? groupIds, CancellationToken ct = default)
    {
        var targets = new List<Guid>();
        foreach (var classId in classIds) targets.Add(classId);
        if (groupIds is { Count: > 0 })
        {
            var all = await db.ClassGroups.AsNoTracking().Select(x => new { x.Id, x.ParentId }).ToListAsync(ct);
            var wanted = new HashSet<Guid>(groupIds);
            foreach (var id in groupIds)
                if (all.All(x => x.Id != id))
                    throw new IdentityOperationException(ApiErrorCodes.NotFound, "包含不存在的分组");
            // 递归收入全部子分组。
            bool added = true;
            while (added)
            {
                added = false;
                foreach (var group in all)
                {
                    if (!wanted.Contains(group.Id) && group.ParentId is { } parent && wanted.Contains(parent))
                    {
                        wanted.Add(group.Id);
                        added = true;
                    }
                }
            }
            var groupedIds = await db.ClassGroupAssignments.AsNoTracking()
                .Where(x => wanted.Contains(x.GroupId))
                .Select(x => x.ClassroomId)
                .ToListAsync(ct);
            targets.AddRange(groupedIds);
        }
        return targets.Distinct().ToList();
    }

    /// <summary>批量操作：返回每个班级的独立结果，单个失败不影响其余班级。</summary>
    public async Task<BatchClassOperationResult> BatchAsync(
        BatchClassOperationRequest request, CancellationToken ct = default)
    {
        var targetIds = await ResolveTargetClassIdsAsync(request.ClassIds, request.GroupIds, ct);
        var results = new List<BatchClassItemResult>();
        foreach (var classId in targetIds)
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

    // ---------- 辅助 ----------

    private async Task<ClassGroup> RequireGroupAsync(Guid id, CancellationToken ct = default) =>
        await db.ClassGroups.SingleOrDefaultAsync(x => x.Id == id, ct)
        ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "分组不存在");

    private async Task<ClassGroup?> RequireGroupIfAnyAsync(Guid? parentId, CancellationToken ct = default) =>
        parentId is not { } id
            ? null
            : await db.ClassGroups.SingleOrDefaultAsync(x => x.Id == id, ct)
              ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "父分组不存在");

    private static int DepthOf(ClassGroup group, IReadOnlyList<ClassGroup> all)
    {
        var depth = 1;
        var parent = group.ParentId;
        while (parent is { } current)
        {
            depth++;
            parent = all.FirstOrDefault(x => x.Id == current)?.ParentId;
        }
        return depth;
    }

    /// <summary>candidate 是否是 ancestor 的后代（用于阻止把分组移入自己的子树）。</summary>
    private static bool IsDescendant(IReadOnlyList<ClassGroup> all, Guid candidate, Guid ancestor)
    {
        var current = all.FirstOrDefault(x => x.Id == candidate)?.ParentId;
        while (current is { } step)
        {
            if (step == ancestor) return true;
            current = all.FirstOrDefault(x => x.Id == step)?.ParentId;
        }
        return false;
    }

    private static string ValidateGroupName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 40)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "分组名称需为 1-40 个字符");
        return trimmed;
    }

    private static string ValidateName(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 40)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "班级名称需为 1-40 个字符");
        return trimmed;
    }
}
