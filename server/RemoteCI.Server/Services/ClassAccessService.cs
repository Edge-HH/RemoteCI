using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 用户与班级的访问关系及按班级的有效权限计算。
/// 系统管理员自动可访问全部班级；普通用户按 ClassMembership 成员关系与班内角色判定；
/// “老师”角色账号额外按显示名绑定课表科目教师名获得任教班级的访问与默认权限。
/// 每个班级的有效权限 = 该班角色默认权限 + 个人新增 − 个人收回；个人调整由本服务按账号 Id 读取，
/// 调用方不必（也不能）自行传入，保证 WebUI、REST、WebSocket 与局域网镜像使用同一份数据。
/// </summary>
public sealed class ClassAccessService(AppDbContext db, TeacherBindingService teachers)
{
    /// <summary>老师账号按显示名绑定任教班级时的身份信息：绑定名、全局角色默认权限与角色名。</summary>
    private sealed record TeacherBinding(string BoundName, UserPermissions RoleDefaults, string RoleName);

    /// <summary>计算班级权限所需的账号信息：个人调整，以及全局角色为内置“老师”时的绑定上下文。</summary>
    private sealed record AccountContext(PermissionAdjustment Adjustment, TeacherBinding? Teacher);

    private async Task<AccountContext> GetAccountContextAsync(Guid userId, CancellationToken ct)
    {
        var info = await db.Users.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new
            {
                x.GrantedPermissions,
                x.RevokedPermissions,
                x.DisplayName,
                RoleKind = x.RoleDefinition!.Kind,
                RoleName = x.RoleDefinition!.Name,
                x.RoleDefinition!.DefaultPermissions,
            })
            .SingleOrDefaultAsync(ct);
        if (info is null) return new AccountContext(PermissionAdjustment.None, null);
        return new AccountContext(
            new PermissionAdjustment(info.GrantedPermissions, info.RevokedPermissions),
            info.RoleKind == AccountRoleKind.Teacher
                ? new TeacherBinding(info.DisplayName, info.DefaultPermissions, info.RoleName)
                : null);
    }

    /// <summary>用户可访问的班级列表（含每班角色与有效权限），供班级切换器与客户端使用。</summary>
    public async Task<IReadOnlyList<ClassSummary>> GetAccessibleClassesAsync(
        Guid userId, UserRole role, CancellationToken ct = default)
    {
        if (role == UserRole.Admin)
        {
            // SQLite 不支持 DateTimeOffset 排序的 SQL 翻译；班级数量极少，取回后内存排序。
            var all = await db.Classrooms
                .Include(x => x.GroupAssignments).ThenInclude(a => a.Group)
                .AsNoTracking().ToListAsync(ct);
            return all.OrderBy(x => x.CreatedAt).Select(x => new ClassSummary
            {
                Id = x.Id,
                Name = x.Name,
                RoleName = "管理员",
                RoleKind = (int)AccountRoleKind.Administrator,
                Permissions = UserPermissions.All,
                VisitorEnabled = x.VisitorAccessEnabled,
                GroupNames = x.GroupAssignments.Select(a => a.Group.Name).ToList(),
                HasAvatar = x.Avatar != null,
            }).ToList();
        }

        var memberships = await db.ClassMemberships.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Join(db.Classrooms, x => x.ClassroomId, y => y.Id, (x, y) => new { Membership = x, Classroom = y })
            .Join(db.AccountRoles, x => x.Membership.RoleDefinitionId, y => y.Id,
                (x, y) => new
                {
                    x.Classroom.Id,
                    x.Classroom.Name,
                    x.Classroom.VisitorAccessEnabled,
                    x.Classroom.CreatedAt,
                    x.Classroom.Avatar,
                    GroupNames = x.Classroom.GroupAssignments.Select(a => a.Group.Name).ToList(),
                    RoleName = y.Name,
                    RoleKind = y.Kind,
                    RoleDefaults = y.DefaultPermissions,
                })
            .ToListAsync(ct);
        var account = await GetAccountContextAsync(userId, ct);
        var results = memberships.OrderBy(x => x.CreatedAt).Select(x => new ClassSummary
        {
            Id = x.Id,
            Name = x.Name,
            RoleName = x.RoleName,
            RoleKind = (int)x.RoleKind,
            Permissions = account.Adjustment.Apply(role, x.RoleDefaults),
            VisitorEnabled = x.VisitorAccessEnabled,
            GroupNames = x.GroupNames,
            HasAvatar = x.Avatar != null,
        }).ToList();

        // 老师按显示名绑定任教班级：成员班级合并任教权限，非成员的任教班级也进入班级列表。
        if (account.Teacher is { } binding)
        {
            var taught = await teachers.GetTaughtClassesAsync(binding.BoundName, ct);
            foreach (var taughtClass in taught)
            {
                var taughtPermissions = account.Adjustment.Apply(role, binding.RoleDefaults);
                var member = results.FirstOrDefault(x => x.Id == taughtClass.ClassId);
                if (member is not null)
                {
                    member.Permissions |= taughtPermissions;
                    continue;
                }
                results.Add(new ClassSummary
                {
                    Id = taughtClass.ClassId,
                    Name = taughtClass.ClassName,
                    RoleName = binding.RoleName,
                    RoleKind = (int)AccountRoleKind.Teacher,
                    Permissions = taughtPermissions,
                    VisitorEnabled = taughtClass.VisitorEnabled,
                    GroupNames = [],
                    HasAvatar = taughtClass.HasAvatar,
                });
            }
        }
        return results;
    }

    public async Task<IReadOnlyList<Guid>> GetAccessibleClassIdsAsync(
        Guid userId, UserRole role, CancellationToken ct = default)
    {
        if (role == UserRole.Admin)
        {
            var ids = await db.Classrooms.AsNoTracking().ToListAsync(ct);
            return ids.OrderBy(x => x.CreatedAt).Select(x => x.Id).ToList();
        }
        var results = await db.ClassMemberships.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.ClassroomId)
            .Select(x => x.ClassroomId)
            .ToListAsync(ct);
        if ((await GetAccountContextAsync(userId, ct)).Teacher is { } binding)
            results.AddRange((await teachers.GetTaughtClassesAsync(binding.BoundName, ct))
                .Select(x => x.ClassId).Except(results));
        return results;
    }

    /// <summary>用户在指定班级的有效权限；非成员且非任教老师返回 None。</summary>
    public async Task<UserPermissions> GetEffectivePermissionsAsync(
        Guid userId, UserRole role, Guid classId, CancellationToken ct = default)
    {
        if (role == UserRole.Admin) return UserPermissions.All;
        var roleDefaults = await db.ClassMemberships.AsNoTracking()
            .Where(x => x.UserId == userId && x.ClassroomId == classId)
            .Join(db.AccountRoles, x => x.RoleDefinitionId, y => y.Id, (x, y) => (UserPermissions?)y.DefaultPermissions)
            .SingleOrDefaultAsync(ct);
        var account = await GetAccountContextAsync(userId, ct);
        // 任教班级的老师：任教权限来自其全局老师角色的默认权限，
        // 与班内成员角色权限取并集，保证班主任等成员身份不被覆盖；两者都套用同一套个人调整。
        var taughtPermissions = account.Teacher is { } binding && teachers.IsTaughtClass(binding.BoundName, classId)
            ? account.Adjustment.Apply(role, binding.RoleDefaults)
            : UserPermissions.None;
        if (roleDefaults is null) return taughtPermissions;
        return account.Adjustment.Apply(role, roleDefaults.Value) | taughtPermissions;
    }

    /// <summary>用户是否可以访问指定班级（访问本身，不含具体权限位判断）。</summary>
    public async Task<bool> CanAccessAsync(Guid userId, UserRole role, Guid classId, CancellationToken ct = default)
    {
        if (role == UserRole.Admin)
            return await db.Classrooms.AnyAsync(x => x.Id == classId, ct);
        if (await db.ClassMemberships.AnyAsync(x => x.UserId == userId && x.ClassroomId == classId, ct)) return true;
        return (await GetAccountContextAsync(userId, ct)).Teacher is { } binding &&
            teachers.IsTaughtClass(binding.BoundName, classId);
    }

    /// <summary>
    /// 是否可以管理班级信息（班名/班头像）：系统管理员，或该班级中班内角色为“班主任”的成员。
    /// </summary>
    public Task<bool> IsClassAdminAsync(Guid userId, UserRole role, Guid classId, CancellationToken ct = default) =>
        role == UserRole.Admin
            ? Task.FromResult(true)
            : db.ClassMemberships.AsNoTracking()
                .Where(x => x.UserId == userId && x.ClassroomId == classId)
                .Join(db.AccountRoles, x => x.RoleDefinitionId, y => y.Id, (x, y) => y.Kind)
                .AnyAsync(kind => kind == AccountRoleKind.ClassAdministrator, ct);

    /// <summary>
    /// 按权限划定的可见班级：系统管理员或拥有对应系统管理权限（查看全部）时返回 null 表示全部；
    /// 其他账号返回自己在其中拥有该班级权限的班级，可能为空。供操作记录、课时统计与设备状态共用。
    /// </summary>
    public async Task<IReadOnlyList<Guid>?> GetPermissionScopeAsync(
        Guid userId, UserRole role, UserPermissions classPermission, ManagementPermissions viewAll,
        CancellationToken ct = default)
    {
        if (role == UserRole.Admin || (await GetManagementPermissionsAsync(userId, role, ct)).HasFlag(viewAll)) return null;
        return (await GetAccessibleClassesAsync(userId, role, ct))
            .Where(x => (x.Permissions ?? UserPermissions.None).HasFlag(classPermission))
            .Select(x => x.Id)
            .ToList();
    }

    /// <summary>账号的系统管理权限：全局角色默认 + 个人新增 − 个人收回；系统管理员为全部。</summary>
    public async Task<ManagementPermissions> GetManagementPermissionsAsync(
        Guid userId, UserRole role, CancellationToken ct = default)
    {
        if (role == UserRole.Admin) return ManagementPermissions.All;
        var info = await db.Users.AsNoTracking()
            .Where(x => x.Id == userId)
            .Select(x => new { x.RoleDefinition!.DefaultManagementPermissions, x.GrantedManagementPermissions, x.RevokedManagementPermissions })
            .SingleOrDefaultAsync(ct);
        return info is null
            ? ManagementPermissions.None
            : RolePermissions.EffectiveManagement(role, info.DefaultManagementPermissions,
                info.GrantedManagementPermissions, info.RevokedManagementPermissions);
    }

    /// <summary>
    /// 用户在指定班级可以自行完成的班级管理操作，由班级权限决定：
    /// “班级信息”允许改班名与头像，“刷新课表”允许从插件拉取课表；系统管理员全部允许。
    /// </summary>
    public async Task<ClassSelfServicePolicy> GetClassSelfServiceAsync(
        Guid userId, UserRole role, Guid classId, CancellationToken ct = default)
    {
        if (role == UserRole.Admin) return ClassSelfServicePolicy.All;
        return ClassSelfServicePolicy.From(await GetEffectivePermissionsAsync(userId, role, classId, ct));
    }

    /// <summary>解析用户未显式选择班级时的默认班级：第一个可访问班级；没有可访问班级返回 null。</summary>
    public async Task<Guid?> ResolveDefaultClassIdAsync(Guid userId, UserRole role, CancellationToken ct = default)
    {
        if (role == UserRole.Admin)
        {
            // 管理员落到最早创建的班级；还没有任何班级时返回 null。
            var classes = await db.Classrooms.AsNoTracking().ToListAsync(ct);
            return classes.OrderBy(x => x.CreatedAt).Select(x => (Guid?)x.Id).FirstOrDefault();
        }
        var membershipClassId = await db.ClassMemberships.AsNoTracking().Where(x => x.UserId == userId)
            .OrderBy(x => x.ClassroomId)
            .Select(x => (Guid?)x.ClassroomId).FirstOrDefaultAsync(ct);
        if (membershipClassId is not null) return membershipClassId;
        // 没有成员关系的老师落到第一个任教班级，保持登录即有所属班级的体验。
        if ((await GetAccountContextAsync(userId, ct)).Teacher is not { } binding) return null;
        return (await teachers.GetTaughtClassesAsync(binding.BoundName, ct))
            .Select(x => (Guid?)x.ClassId).FirstOrDefault();
    }

    /// <summary>按成员角色的有效权限：与全局角色同一套规则（ViewCurrentCourse 为底 + 角色默认 + 个人新增 − 个人收回）。</summary>
    public static UserPermissions EffectiveForMembership(
        UserRole role, UserPermissions membershipRoleDefaults, PermissionAdjustment adjustment) =>
        adjustment.Apply(role, membershipRoleDefaults);
}

/// <summary>账号相对角色默认权限的个人调整：新增与收回。同一套调整在每个班级都按该班的班内角色生效。</summary>
public readonly record struct PermissionAdjustment(UserPermissions Granted, UserPermissions Revoked)
{
    public static PermissionAdjustment None => default;

    public static PermissionAdjustment Of(AppUser user) => new(user.GrantedPermissions, user.RevokedPermissions);

    public UserPermissions Apply(UserRole role, UserPermissions roleDefaults) =>
        RolePermissions.Effective(role, Granted, roleDefaults, Revoked);
}

/// <summary>按账号实体计算的全局权限；调用方须已加载 <see cref="AppUser.RoleDefinition"/>（未加载时只有系统管理员视为有权限）。</summary>
public static class AccountPermissions
{
    public static UserPermissions Global(AppUser user) =>
        PermissionAdjustment.Of(user).Apply(user.Role, user.RoleDefinition?.DefaultPermissions ?? UserPermissions.None);

    public static ManagementPermissions Management(AppUser user) =>
        RolePermissions.EffectiveManagement(user.Role,
            user.RoleDefinition?.DefaultManagementPermissions ?? ManagementPermissions.None,
            user.GrantedManagementPermissions, user.RevokedManagementPermissions);

    public static bool HasManagement(AppUser user, ManagementPermissions permission) =>
        Management(user).HasFlag(permission);
}
