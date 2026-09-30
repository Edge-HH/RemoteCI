using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 批量导入人员：行格式 <c>ID,用户名,班级,角色,密码</c>（密码可空=待激活账号，首次登录强制设置密码；
/// 班级不存在自动创建；角色按名称匹配角色预设）。兼容 3 列旧格式 <c>ID,用户名,密码</c>，班级/角色取默认值。
/// </summary>
public sealed class UserImportService(
    IdentityCoordinator identities,
    ClassroomService classrooms,
    AccountRoleService roleService)
{
    public async Task<BatchImportResult> ImportAsync(
        string? text, Guid? defaultClassId, Guid? defaultRoleId, CancellationToken ct = default)
    {
        var separators = new[] { '\n', '\r' };
        var lines = (text ?? string.Empty).Split(separators)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
        if (lines.Count == 0)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "请粘贴至少一行账号信息。");

        var created = 0;
        var failures = new List<string>();
        foreach (var line in lines)
        {
            var parts = line.Split(',', '\t', StringSplitOptions.TrimEntries)
                .Where(part => part.Length > 0).ToArray();
            if (parts.Length is < 3 or > 5)
            {
                failures.Add($"列数无效（应为 ID,用户名,班级,角色,密码）：{Truncate(line, 48)}");
                continue;
            }
            var username = parts[0];
            var displayName = parts[1];
            // 5 列：ID,用户名,班级,角色,密码（密码可空）；4 列：ID,用户名,班级,角色（待激活）；
            // 3 列旧格式：ID,用户名,密码，班级/角色取默认值。
            var className = parts.Length >= 4 ? parts[2] : null;
            var roleName = parts.Length >= 4 ? parts[3] : null;
            var password = parts.Length switch
            {
                5 => parts[4],
                4 => string.Empty,
                _ => parts[2],
            };
            try
            {
                Guid classId;
                if (className is not null)
                {
                    classId = (await classrooms.EnsureClassByNameAsync(className, ct)).Id;
                }
                else
                {
                    classId = defaultClassId
                        ?? throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "行内缺少班级，且未提供默认班级");
                }

                var roleId = defaultRoleId ?? AccountRole.StudentId;
                if (roleName is not null)
                {
                    var role = await roleService.FindByNameAsync(roleName, ct)
                        ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, $"角色“{roleName}”不存在");
                    roleId = role.Id;
                }

                var createdUser = await identities.CreateUserAsync(new CreateUserRequest
                {
                    Username = username,
                    DisplayName = displayName,
                    Password = password,
                    Role = UserRole.User,
                    RoleId = roleId,
                    GrantedPermissions = UserPermissions.None,
                }, ct);
                await classrooms.AddMemberAsync(classId, createdUser.Id, roleId, ct);
                created++;
            }
            catch (IdentityOperationException ex)
            {
                failures.Add($"{username} — {ex.Message}");
            }
        }
        return new BatchImportResult { Created = created, Failures = failures };
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
