using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 用户与班级的访问关系及按班级的有效权限计算。
/// 系统管理员自动可访问全部班级；普通用户按 ClassMembership 成员关系与班内角色判定。
/// 方法以 userId/role/granted 表达身份，REST 主体（UserProfile）与页面实体（AppUser）都能调用。
/// </summary>
public sealed class ClassAccessService(AppDbContext db)
{
    /// <summary>用户可访问的班级列表（含每班角色与有效权限），供班级切换器与客户端使用。</summary>
    public async Task<IReadOnlyList<ClassSummary>> GetAccessibleClassesAsync(
        Guid userId, UserRole role, UserPermissions granted = UserPermissions.None, CancellationToken ct = default)
    {
        if (role == UserRole.Admin)
        {
            // SQLite 不支持 DateTimeOffset 排序的 SQL 翻译；班级数量极少，取回后内存排序。
            var all = await db.Classrooms.AsNoTracking().ToListAsync(ct);
            return all.OrderBy(x => x.CreatedAt).Select(x => new ClassSummary
            {
                Id = x.Id,
                Name = x.Name,
                RoleName = "管理员",
                Permissions = UserPermissions.All,
                VisitorEnabled = x.VisitorAccessEnabled,
            }).ToList();
        }

        var memberships = await db.ClassMemberships.AsNoTracking()
            .Where(x => x.UserId == userId)
            .Join(db.Classrooms, x => x.ClassroomId, y => y.Id, (x, y) => new { Membership = x, Classroom = y })
            .Join(db.AccountRoles, x => x.Membership.RoleDefinitionId, y => y.Id,
                (x, y) => new { x.Classroom.Id, x.Classroom.Name, x.Classroom.VisitorAccessEnabled, x.Classroom.CreatedAt, RoleName = y.Name, RoleDefaults = y.DefaultPermissions })
            .ToListAsync(ct);
        return memberships.OrderBy(x => x.CreatedAt).Select(x => new ClassSummary
        {
            Id = x.Id,
            Name = x.Name,
            RoleName = x.RoleName,
            Permissions = EffectiveForMembership(role, x.RoleDefaults, granted),
            VisitorEnabled = x.VisitorAccessEnabled,
        }).ToList();
    }

    public async Task<IReadOnlyList<Guid>> GetAccessibleClassIdsAsync(
        Guid userId, UserRole role, CancellationToken ct = default)
    {
        if (role == UserRole.Admin)
        {
            var ids = await db.Classrooms.AsNoTracking().ToListAsync(ct);
            return ids.OrderBy(x => x.CreatedAt).Select(x => x.Id).ToList();
        }
        return await db.ClassMemberships.AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderBy(x => x.ClassroomId)
            .Select(x => x.ClassroomId)
            .ToListAsync(ct);
    }

    /// <summary>用户在指定班级的有效权限；非成员返回 None。</summary>
    public async Task<UserPermissions> GetEffectivePermissionsAsync(
        Guid userId, UserRole role, Guid classId, UserPermissions granted = UserPermissions.None, CancellationToken ct = default)
    {
        if (role == UserRole.Admin) return UserPermissions.All;
        var roleDefaults = await db.ClassMemberships.AsNoTracking()
            .Where(x => x.UserId == userId && x.ClassroomId == classId)
            .Join(db.AccountRoles, x => x.RoleDefinitionId, y => y.Id, (x, y) => (UserPermissions?)y.DefaultPermissions)
            .SingleOrDefaultAsync(ct);
        return roleDefaults is null ? UserPermissions.None : EffectiveForMembership(role, roleDefaults.Value, granted);
    }

    /// <summary>用户是否可以访问指定班级（访问本身，不含具体权限位判断）。</summary>
    public Task<bool> CanAccessAsync(Guid userId, UserRole role, Guid classId, CancellationToken ct = default) =>
        role == UserRole.Admin
            ? db.Classrooms.AnyAsync(x => x.Id == classId, ct)
            : db.ClassMemberships.AnyAsync(x => x.UserId == userId && x.ClassroomId == classId, ct);

    /// <summary>解析用户未显式选择班级时的默认班级：第一个可访问班级；没有可访问班级返回 null。</summary>
    public async Task<Guid?> ResolveDefaultClassIdAsync(Guid userId, UserRole role, CancellationToken ct = default)
    {
        if (role == UserRole.Admin)
        {
            // 管理员优先落默认班级，保持单班级部署的管理体验。
            var classes = await db.Classrooms.AsNoTracking().ToListAsync(ct);
            return classes.OrderBy(x => x.Id == Classroom.DefaultId ? 0 : 1).ThenBy(x => x.CreatedAt)
                .Select(x => (Guid?)x.Id).FirstOrDefault();
        }
        return await db.ClassMemberships.AsNoTracking().Where(x => x.UserId == userId)
            .OrderBy(x => x.ClassroomId)
            .Select(x => (Guid?)x.ClassroomId).FirstOrDefaultAsync(ct);
    }

    /// <summary>按成员角色的有效权限：与全局角色同一套规则（ViewCurrentCourse 为底 + 角色默认 + 个人授予）。</summary>
    public static UserPermissions EffectiveForMembership(
        UserRole role, UserPermissions membershipRoleDefaults, UserPermissions granted = UserPermissions.None) =>
        role == UserRole.Admin
            ? UserPermissions.All
            : RolePermissions.Effective(UserRole.User, granted, membershipRoleDefaults);
}
