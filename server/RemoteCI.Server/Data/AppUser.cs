using Microsoft.AspNetCore.Identity;
using RemoteCI.Shared;

namespace RemoteCI.Server.Data;

public sealed class AppUser : IdentityUser<Guid>, UserProfileLike
{
    public string DisplayName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.User;
    public Guid RoleDefinitionId { get; set; } = AccountRole.StudentId;
    public AccountRole RoleDefinition { get; set; } = null!;
    /// <summary>相对角色默认权限新增的班级权限；在每个班级都叠加到该班的班内角色上。</summary>
    public UserPermissions GrantedPermissions { get; set; }
    /// <summary>相对角色默认权限收回的班级权限；在每个班级都从该班的班内角色中扣除。</summary>
    public UserPermissions RevokedPermissions { get; set; }
    /// <summary>相对全局角色新增的系统管理权限。</summary>
    public ManagementPermissions GrantedManagementPermissions { get; set; }
    /// <summary>相对全局角色收回的系统管理权限。</summary>
    public ManagementPermissions RevokedManagementPermissions { get; set; }
    public bool Enabled { get; set; } = true;
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// 首次进入 WebUI 时创建的系统管理员（部署所有者）。全站只有一个，始终是启用的管理员；
    /// 其他管理员不能编辑、停用、重置密码、删除它或替它管理 API Key，只能由本人维护。
    /// </summary>
    public bool IsSystemOwner { get; set; }

    /// <summary>批量导入时未设置密码：首次登录强制设置密码，设置完成后清除。</summary>
    public bool PasswordPending { get; set; }

    /// <summary>TOTP 密钥由服务器 Data Protection 密钥环加密，并绑定账号 ID。</summary>
    public string? ProtectedTotpKey { get; set; }
    public long? LastTotpTimeStep { get; set; }
    /// <summary>恢复码仅保存 SHA-256 摘要；使用后原子删除。</summary>
    public string? TwoFactorRecoveryHashes { get; set; }

    /// <summary>首登设置密码的一次性令牌摘要（SHA-256）；15 分钟有效。</summary>
    public string? SetupTokenHash { get; set; }
    public DateTimeOffset? SetupTokenExpiresAt { get; set; }
}
