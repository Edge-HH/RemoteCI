using Microsoft.AspNetCore.Identity;
using RemoteCI.Shared;

namespace RemoteCI.Server.Data;

public sealed class AppUser : IdentityUser<Guid>, UserProfileLike
{
    public string DisplayName { get; set; } = string.Empty;
    public UserRole Role { get; set; } = UserRole.User;
    public Guid RoleDefinitionId { get; set; } = AccountRole.StudentId;
    public AccountRole RoleDefinition { get; set; } = null!;
    public UserPermissions GrantedPermissions { get; set; }
    public bool Enabled { get; set; } = true;
    public long Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>批量导入时未设置密码：首次登录强制设置密码，设置完成后清除。</summary>
    public bool PasswordPending { get; set; }

    /// <summary>首登设置密码的一次性令牌摘要（SHA-256）；15 分钟有效。</summary>
    public string? SetupTokenHash { get; set; }
    public DateTimeOffset? SetupTokenExpiresAt { get; set; }
}
