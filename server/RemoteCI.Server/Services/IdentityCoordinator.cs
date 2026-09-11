using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 账号、权限和设备会话的唯一入口，所有安全状态变更都在同一数据库事务中完成。
/// </summary>
public sealed partial class IdentityCoordinator(
    AppDbContext db,
    UserManager<AppUser> users,
    ExtensionPolicyService extensionPolicies,
    IOptions<ServerOptions> options,
    ILogger<IdentityCoordinator> logger)
{
    private readonly ServerOptions _options = options.Value;

    /// <summary>插件配对码有效期：遗忘在聊天记录/页面上的配对码不能永久可用。</summary>
    private static readonly TimeSpan PairCodeLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 当前服务端进程实例标识，随授权镜像下发。服务端重启或数据库重建后变化，
    /// 供插件在镜像版本号回退时识别实例变化并强制覆盖，避免旧镜像永久滞留。
    /// </summary>
    public static Guid InstanceId { get; } = Guid.NewGuid();

    /// <summary>管理员角色的变更串行化：保证“最后管理员”检查与提交之间没有其他管理员变更插入。</summary>
    private static readonly SemaphoreSlim AdminMutationGate = new(1, 1);

    /// <summary>每个账号同时保持的设备会话上限，超出时撤销最早创建的会话。</summary>
    private const int MaxActiveSessionsPerUser = 20;

    public async Task BootstrapAsync(CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR IGNORE INTO SystemMetadata (Id, AccountVersion) VALUES (1, 0);", ct);
        var now = DateTimeOffset.UtcNow;
        if (!await db.AccountRoles.AnyAsync(ct))
        {
            db.AccountRoles.AddRange(
                new AccountRole { Id = AccountRole.StudentId, Name = "Student", NormalizedName = "STUDENT", Kind = AccountRoleKind.Student, DefaultPermissions = UserPermissions.None, CreatedAt = now, UpdatedAt = now },
                new AccountRole { Id = AccountRole.AdministratorId, Name = "Administrator", NormalizedName = "ADMINISTRATOR", Kind = AccountRoleKind.Administrator, DefaultPermissions = UserPermissions.All, CreatedAt = now, UpdatedAt = now });
        }
        if (!await db.BackupConfigurations.AnyAsync(ct)) db.BackupConfigurations.Add(new BackupConfiguration());
        await db.SaveChangesAsync(ct);
        await db.AccountRoles.Where(x => x.Id == AccountRole.StudentId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Name, "学生")
            .SetProperty(x => x.NormalizedName, "学生"), ct);
        await db.AccountRoles.Where(x => x.Id == AccountRole.AdministratorId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Name, "管理员")
            .SetProperty(x => x.NormalizedName, "管理员")
            .SetProperty(x => x.DefaultPermissions, UserPermissions.All), ct);

        // 启动时清理过期超过 30 天的会话行，避免 DeviceSessions 表长期无界增长。
        // SQLite 不支持 DateTimeOffset 比较的 SQL 翻译，先投影再在内存过滤。
        var staleThreshold = DateTimeOffset.UtcNow.AddDays(-30);
        var staleIds = (await db.DeviceSessions.Select(x => new { x.Id, x.ExpiresAt }).ToListAsync(ct))
            .Where(x => x.ExpiresAt < staleThreshold)
            .Select(x => x.Id)
            .ToList();
        foreach (var chunk in staleIds.Chunk(500))
            await db.DeviceSessions.Where(x => chunk.Contains(x.Id)).ExecuteDeleteAsync(ct);

        if (!await users.Users.AnyAsync(ct))
        {
            var configuredPassword = FirstNonEmpty(
                Environment.GetEnvironmentVariable("REMOTECI_ADMIN_PASSWORD"),
                _options.BootstrapAdminPassword);
            var generatedPassword = configuredPassword is null;
            var password = configuredPassword ?? CreateReadableSecret(18);
            ValidatePassword(password);
            var admin = new AppUser
            {
                Id = Guid.NewGuid(),
                UserName = _options.BootstrapAdminUsername,
                DisplayName = "系统管理员",
                Role = UserRole.Admin,
                RoleDefinitionId = AccountRole.AdministratorId,
                GrantedPermissions = UserPermissions.None,
                Enabled = true,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            admin.Version = await NextVersionAsync(ct);
            EnsureIdentitySucceeded(await users.CreateAsync(admin, password));
            if (generatedPassword && _options.LogBootstrapSecrets)
            {
                logger.LogWarning("首次启动已创建管理员 {Username}，一次性初始密码：{Password}。请立即登录并修改密码。",
                    admin.UserName, password);
            }
            else if (generatedPassword)
            {
                logger.LogWarning("首次启动已创建管理员 {Username}，一次性初始密码按配置不写入日志。",
                    admin.UserName);
            }
            else
            {
                logger.LogInformation("首次启动已使用外部配置创建管理员 {Username}。", admin.UserName);
            }
        }

        if (!await db.PluginCredentials.AnyAsync(ct) && !await db.PluginPairingCodes.AnyAsync(ct))
        {
            var configuredCode = FirstNonEmpty(
                Environment.GetEnvironmentVariable("REMOTECI_PLUGIN_PAIR_CODE"),
                _options.BootstrapPluginPairCode);
            var generatedCode = configuredCode is null;
            var code = configuredCode ?? CreateReadableSecret(12);
            // 环境变量注入的引导配对码是部署凭据，保持长期有效；自动生成的限时 30 分钟。
            await AddPairingCodeAsync(code, ct, timeLimited: generatedCode);
            if (generatedCode && _options.LogBootstrapSecrets)
                logger.LogWarning("首次启动插件一次性配对码：{PairCode}。该码使用后立即失效。", code);
            else if (generatedCode)
                logger.LogWarning("首次启动插件一次性配对码已生成，按配置不写入日志。");
            else
                logger.LogInformation("首次启动已使用外部配置初始化插件一次性配对码。");
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    public async Task<AuthResponse> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        var user = await users.FindByNameAsync(request.Username.Trim());
        if (user is null || !user.Enabled)
        {
            // 恒定时间：对不存在的账号也执行一次同等成本的 PBKDF2 校验，避免响应时间泄露用户名是否存在。
            await users.CheckPasswordAsync(TimingUser(), request.Password);
            throw new IdentityOperationException(ApiErrorCodes.Unauthorized, "ID 或密码错误");
        }
        if (await users.IsLockedOutAsync(user))
            throw new IdentityOperationException(ApiErrorCodes.Unauthorized, "失败次数过多，账号已临时锁定，请稍后再试");
        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            // 接入 Identity 锁定：连续失败 8 次锁定 10 分钟（与 WebUI 登录行为一致）。
            await users.AccessFailedAsync(user);
            throw new IdentityOperationException(ApiErrorCodes.Unauthorized, "ID 或密码错误");
        }
        await users.ResetAccessFailedCountAsync(user);
        return await CreateOrRotateSessionAsync(user, request.DeviceName, null, ct);
    }

    public async Task<AuthResponse> RefreshAsync(RefreshSessionRequest request, CancellationToken ct = default)
    {
        var verifier = Hash(request.DeviceSecret);
        var session = await db.DeviceSessions.Include(x => x.User).ThenInclude(x => x.RoleDefinition)
            .SingleOrDefaultAsync(x => x.Id == request.DeviceSessionId, ct);
        if (session is null || session.RevokedAt is not null || session.ExpiresAt <= DateTimeOffset.UtcNow ||
            !session.User.Enabled || !FixedEquals(session.VerifierHash, verifier))
            throw new IdentityOperationException(ApiErrorCodes.Unauthorized, "设备会话已失效");

        return await CreateOrRotateSessionAsync(session.User, session.DeviceName, session, ct);
    }

    public async Task<AuthPrincipal?> ValidateAccessTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = Hash(token);
        var session = await db.DeviceSessions.Include(x => x.User).ThenInclude(x => x.RoleDefinition)
            .SingleOrDefaultAsync(x => x.AccessTokenHash == hash, ct);
        if (session is null || session.RevokedAt is not null || session.AccessExpiresAt <= DateTimeOffset.UtcNow ||
            session.ExpiresAt <= DateTimeOffset.UtcNow || !session.User.Enabled)
            return null;

        // WS 高频消息每条都写 LastSeenAt 会造成 SQLite 写放大与锁竞争；最多每分钟落库一次。
        var now = DateTimeOffset.UtcNow;
        if (now - session.LastSeenAt > TimeSpan.FromMinutes(1))
        {
            session.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
        }
        return new AuthPrincipal(
            PeerRole.Watch,
            await ToProfileAsync(session.User, ct),
            session.Id,
            PluginCredentialId: null,
            ValidUntil: session.AccessExpiresAt < session.ExpiresAt ? session.AccessExpiresAt : session.ExpiresAt);
    }

    public async Task<AuthPrincipal?> ValidatePluginTokenAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = Hash(token);
        var credential = await db.PluginCredentials.SingleOrDefaultAsync(x => x.TokenHash == hash && x.Enabled, ct);
        if (credential is null) return null;
        var now = DateTimeOffset.UtcNow;
        if (now - credential.LastSeenAt > TimeSpan.FromMinutes(1))
        {
            credential.LastSeenAt = now;
            await db.SaveChangesAsync(ct);
        }
        return new AuthPrincipal(
            PeerRole.Plugin,
            User: null,
            DeviceSessionId: null,
            PluginCredentialId: credential.Id,
            ValidUntil: null);
    }

    public async Task<AuthPrincipal?> ValidateAnyTokenAsync(string token, CancellationToken ct = default) =>
        await ValidatePluginTokenAsync(token, ct) ?? await ValidateAccessTokenAsync(token, ct);

    public async Task<IReadOnlyList<PluginCredentialInfo>> ListPluginCredentialsAsync(CancellationToken ct = default)
    {
        // SQLite 不支持 DateTimeOffset 排序的 SQL 翻译；凭证数量极少，取回后在内存排序。
        var credentials = await db.PluginCredentials.AsNoTracking().ToListAsync(ct);
        return credentials
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new PluginCredentialInfo
            {
                Id = x.Id,
                Name = x.Name,
                CreatedAt = x.CreatedAt,
                LastSeenAt = x.LastSeenAt,
                Enabled = x.Enabled,
            }).ToList();
    }

    /// <summary>吊销插件长期凭据；调用方随后按凭据 ID 主动断开在线连接。</summary>
    public async Task RevokePluginCredentialAsync(Guid id, CancellationToken ct = default)
    {
        var credential = await db.PluginCredentials.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "插件凭证不存在");
        if (!credential.Enabled) return; // 幂等。
        credential.Enabled = false;
        await db.SaveChangesAsync(ct);
    }

    public async Task<PairResponse> PairPluginAsync(PairRequest request, CancellationToken ct = default)
    {
        var hash = Hash(request.PairCode);
        var now = DateTimeOffset.UtcNow;
        // SQLite 不支持 DateTimeOffset 比较的 SQL 翻译，先按哈希取候选再在内存过滤过期。
        var candidates = await db.PluginPairingCodes.Where(x => x.CodeHash == hash).ToListAsync(ct);
        var candidate = candidates.FirstOrDefault(x => x.UsedAt is null && x.ExpiresAt > now)
            ?? throw new IdentityOperationException(ApiErrorCodes.PairCodeInvalid, "插件配对码无效、已使用或已过期");
        // 原子消费配对码：并发请求中只有一个能把 UsedAt 从未置位更新为当前时间，
        // 防止两个请求同时读到“未使用”的配对码并各自签发插件凭证。
        var consumed = await db.PluginPairingCodes
            .Where(x => x.Id == candidate.Id && x.UsedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UsedAt, now), ct);
        if (consumed == 0)
            throw new IdentityOperationException(ApiErrorCodes.PairCodeInvalid, "插件配对码无效、已使用或已过期");

        var token = CreateSecret(32);
        db.PluginCredentials.Add(new PluginCredential
        {
            Id = Guid.NewGuid(),
            Name = "ClassIsland 插件",
            TokenHash = Hash(token),
            CreatedAt = now,
            LastSeenAt = now,
            Enabled = true,
        });
        await db.SaveChangesAsync(ct);
        return new PairResponse { Token = token, Role = "plugin", ExpiresAt = null };
    }

    public async Task<string> CreatePluginPairingCodeAsync(CancellationToken ct = default)
    {
        var code = CreateReadableSecret(12);
        await AddPairingCodeAsync(code, ct);
        return code;
    }

    public async Task<UserProfile?> GetProfileAsync(Guid id, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(id.ToString());
        return user is null || !user.Enabled ? null : await ToProfileAsync(user, ct);
    }

    /// <summary>
    /// 读取目标账号角色（不区分启用状态）。管理守卫必须用它而非 GetProfileAsync：
    /// 后者对禁用账号返回 null，会让普通用户绕过“仅管理员可管理管理员”检查接管被禁用的管理员。
    /// </summary>
    public async Task<UserRole?> GetRoleAsync(Guid id, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(id.ToString());
        return user?.Role;
    }

    public async Task<IReadOnlyList<UserListItem>> ListUsersAsync(CancellationToken ct = default) =>
        await users.Users.Include(x => x.RoleDefinition).OrderByDescending(x => x.Role).ThenBy(x => x.UserName)
            .Select(x => new UserListItem
            {
                Id = x.Id,
                Username = x.UserName!,
                DisplayName = x.DisplayName,
                Role = x.Role,
                RoleId = x.RoleDefinitionId,
                RoleName = x.RoleDefinition.Name,
                GrantedPermissions = x.GrantedPermissions,
                EffectivePermissions = x.Role == UserRole.Admin
                    ? UserPermissions.All
                    : UserPermissions.ViewCurrentCourse | x.RoleDefinition.DefaultPermissions | x.GrantedPermissions,
                Enabled = x.Enabled,
                UpdatedAt = x.UpdatedAt,
            }).ToListAsync(ct);

    public async Task<UserListItem> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        ValidateUserInput(request.Username, request.DisplayName, request.Password, request.Role);
        var role = await ResolveRoleAsync(request.RoleId, request.Role, ct);
        var protocolRole = role.Kind == AccountRoleKind.Administrator ? UserRole.Admin : UserRole.User;
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = request.Username.Trim(),
            DisplayName = request.DisplayName.Trim(),
            Role = protocolRole,
            RoleDefinitionId = role.Id,
            GrantedPermissions = NormalizeGrants(protocolRole, request.GrantedPermissions),
            Enabled = true,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        user.Version = await NextVersionAsync(ct);
        EnsureIdentitySucceeded(await users.CreateAsync(user, request.Password));
        return await ToListItemAsync(user, ct);
    }

    public async Task<UserListItem> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default)
    {
        ValidateDisplayName(request.DisplayName);
        ValidateRole(request.Role);
        await AdminMutationGate.WaitAsync(ct);
        try
        {
            var user = await RequireUserAsync(id);
            var targetRole = await ResolveRoleAsync(request.RoleId, request.Role, ct);
            var targetProtocolRole = targetRole.Kind == AccountRoleKind.Administrator ? UserRole.Admin : UserRole.User;
            if (user.Role == UserRole.Admin && user.Enabled && (targetProtocolRole != UserRole.Admin || !request.Enabled))
                await GuardLastAdminAsync(user.Id, ct);

            var mustRevoke = user.Enabled && !request.Enabled;
            user.DisplayName = request.DisplayName.Trim();
            user.Role = targetProtocolRole;
            user.RoleDefinitionId = targetRole.Id;
            user.GrantedPermissions = NormalizeGrants(targetProtocolRole, request.GrantedPermissions);
            user.Enabled = request.Enabled;
            user.UpdatedAt = DateTimeOffset.UtcNow;
            user.Version = await NextVersionAsync(ct);
            EnsureIdentitySucceeded(await users.UpdateAsync(user));
            if (mustRevoke) await RevokeAllSessionsAsync(user.Id, ct);
            return await ToListItemAsync(user, ct);
        }
        finally
        {
            AdminMutationGate.Release();
        }
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken ct = default)
    {
        await AdminMutationGate.WaitAsync(ct);
        try
        {
            var user = await RequireUserAsync(id);
            if (user.Role == UserRole.Admin && user.Enabled) await GuardLastAdminAsync(user.Id, ct);
            EnsureIdentitySucceeded(await users.DeleteAsync(user));
            await NextVersionAsync(ct);
        }
        finally
        {
            AdminMutationGate.Release();
        }
    }

    public async Task ResetPasswordAsync(Guid id, string password, CancellationToken ct = default)
    {
        ValidatePassword(password);
        var user = await RequireUserAsync(id);
        var token = await users.GeneratePasswordResetTokenAsync(user);
        EnsureIdentitySucceeded(await users.ResetPasswordAsync(user, token, password));
        await users.UpdateSecurityStampAsync(user);
        user.UpdatedAt = DateTimeOffset.UtcNow;
        user.Version = await NextVersionAsync(ct);
        EnsureIdentitySucceeded(await users.UpdateAsync(user));
        await RevokeAllSessionsAsync(id, ct);
    }

    public async Task ChangePasswordAsync(Guid id, ChangePasswordRequest request, CancellationToken ct = default)
    {
        ValidatePassword(request.NewPassword);
        var user = await RequireUserAsync(id);
        EnsureIdentitySucceeded(await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword));
        user.UpdatedAt = DateTimeOffset.UtcNow;
        user.Version = await NextVersionAsync(ct);
        EnsureIdentitySucceeded(await users.UpdateAsync(user));
        await RevokeAllSessionsAsync(id, ct);
    }

    public async Task<IReadOnlyList<DeviceSessionSummary>> ListSessionsAsync(Guid userId, Guid? currentId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var sessions = await db.DeviceSessions.Where(x => x.UserId == userId && x.RevokedAt == null).ToListAsync(ct);
        return sessions.Where(x => x.ExpiresAt > now).OrderByDescending(x => x.LastSeenAt)
            .Select(x => new DeviceSessionSummary
            {
                Id = x.Id,
                DeviceName = x.DeviceName,
                CreatedAt = x.CreatedAt,
                LastSeenAt = x.LastSeenAt,
                ExpiresAt = x.ExpiresAt,
                Current = currentId == x.Id,
            }).ToList();
    }

    public async Task RevokeSessionAsync(Guid ownerId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await db.DeviceSessions.SingleOrDefaultAsync(x => x.Id == sessionId && x.UserId == ownerId, ct)
            ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "设备会话不存在");
        session.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await NextVersionAsync(ct);
    }

    public async Task<AccountSync> CreateSyncAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var accountVersion = await db.SystemMetadata.Where(x => x.Id == 1).Select(x => x.AccountVersion).SingleAsync(ct);
        var accounts = await users.Users.Include(x => x.RoleDefinition).Select(x => new SyncedAccount
        {
            Id = x.Id,
            Username = x.UserName!,
            DisplayName = x.DisplayName,
            Role = x.Role,
            RoleId = x.RoleDefinitionId,
            RoleName = x.RoleDefinition.Name,
            GrantedPermissions = x.GrantedPermissions,
            EffectivePermissions = x.Role == UserRole.Admin
                ? UserPermissions.All
                : UserPermissions.ViewCurrentCourse | x.RoleDefinition.DefaultPermissions | x.GrantedPermissions,
            Enabled = x.Enabled,
            Version = x.Version,
        }).ToListAsync(ct);
        var extensionPolicyRows = await db.ExtensionPolicies.AsNoTracking()
            .Where(x => x.Enabled).ToListAsync(ct);
        var hiddenByUser = (await db.UserExtensionPreferences.AsNoTracking()
                .Where(x => !x.ShowOnWatch)
                .Select(x => new { x.UserId, x.ExtensionId })
                .ToListAsync(ct))
            .GroupBy(x => x.UserId)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<string>)group.Select(x => x.ExtensionId).ToArray());
        foreach (var account in accounts)
        {
            var profile = account.ToProfile();
            ExtensionPolicyService.ApplyAccess(
                profile,
                extensionPolicyRows,
                hiddenByUser.GetValueOrDefault(account.Id, Array.Empty<string>()));
            account.AllowedExtensionIds = profile.AllowedExtensionIds;
            account.VisibleExtensionIds = profile.VisibleExtensionIds;
        }
        var activeSessions = await db.DeviceSessions.Include(x => x.User).ThenInclude(x => x.RoleDefinition)
            .Where(x => x.RevokedAt == null).ToListAsync(ct);
        var sessions = activeSessions.Where(x => x.ExpiresAt > now && x.User.Enabled)
            .Select(x => new SyncedDeviceSession
            {
                Id = x.Id,
                UserId = x.UserId,
                Verifier = x.VerifierHash,
                ExpiresAt = x.ExpiresAt,
            }).ToList();
        return new AccountSync
        {
            Version = accountVersion,
            ServerInstanceId = InstanceId,
            ServerVersion = AppVersion.Version,
            ServerCapabilities = RemoteCiCapabilities.Current.ToList(),
            GeneratedAt = now,
            Accounts = accounts,
            Sessions = sessions,
        };
    }

    private async Task<AuthResponse> CreateOrRotateSessionAsync(
        AppUser user, string deviceName, DeviceSession? existing, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var accessToken = CreateSecret(32);
        var deviceSecret = CreateSecret(32);
        var session = existing ?? new DeviceSession
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DeviceName = NormalizeDeviceName(deviceName),
            CreatedAt = now,
        };
        session.AccessTokenHash = Hash(accessToken);
        session.VerifierHash = Hash(deviceSecret);
        session.AccessExpiresAt = now + _options.AccessTokenTtl;
        session.ExpiresAt = now + _options.DeviceSessionTtl;
        session.LastSeenAt = now;
        session.RevokedAt = null;
        if (existing is null)
        {
            db.DeviceSessions.Add(session);
            // 每账号活跃会话上限：超出时撤销最早创建的一批，防止长周期累积。
            // SQLite 不支持 DateTimeOffset 比较的 SQL 翻译，先取未撤销会话再在内存中过滤。
            var candidates = await db.DeviceSessions
                .Where(x => x.UserId == user.Id && x.RevokedAt == null)
                .ToListAsync(ct);
            var overflow = candidates
                .Where(x => x.ExpiresAt > now)
                .OrderByDescending(x => x.CreatedAt)
                .Skip(MaxActiveSessionsPerUser - 1)
                .ToList();
            foreach (var old in overflow) old.RevokedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await NextVersionAsync(ct);
        return new AuthResponse
        {
            AccessToken = accessToken,
            AccessExpiresAt = session.AccessExpiresAt,
            DeviceSessionId = session.Id,
            DeviceSecret = deviceSecret,
            DeviceExpiresAt = session.ExpiresAt,
            User = await ToProfileAsync(user, ct),
        };
    }

    private async Task AddPairingCodeAsync(string code, CancellationToken ct, bool timeLimited = true)
    {
        var now = DateTimeOffset.UtcNow;
        db.PluginPairingCodes.Add(new PluginPairingCode
        {
            Id = Guid.NewGuid(),
            CodeHash = Hash(code),
            CreatedAt = now,
            // WebUI 生成的配对码限时 30 分钟；一次性消费仍由 UsedAt 原子控制。
            ExpiresAt = timeLimited ? now.Add(PairCodeLifetime) : DateTimeOffset.MaxValue,
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<AppUser> RequireUserAsync(Guid id) => await users.FindByIdAsync(id.ToString())
        ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "用户不存在");

    private async Task GuardLastAdminAsync(Guid id, CancellationToken ct)
    {
        var otherAdmins = await users.Users.CountAsync(x => x.Id != id && x.Enabled && x.Role == UserRole.Admin, ct);
        if (otherAdmins == 0) throw new IdentityOperationException(ApiErrorCodes.LastAdmin, "不能删除、禁用或降级最后一个管理员");
    }

    private async Task RevokeAllSessionsAsync(Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await db.DeviceSessions.Where(x => x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(x => x.SetProperty(s => s.RevokedAt, now), ct);
    }

    /// <summary>读取全局“强制在标题显示发送人”设置，默认开启以保持既有行为。</summary>
    public async Task<bool> GetForceSenderInTitleAsync(CancellationToken ct = default)
    {
        var metadata = await db.SystemMetadata.AsNoTracking().SingleAsync(x => x.Id == 1, ct);
        return metadata.ForceSenderInTitle;
    }

    /// <summary>更新全局“强制在标题显示发送人”设置，返回可广播给在线手表的快照。</summary>
    public async Task<SettingsSync> SetForceSenderInTitleAsync(bool force, CancellationToken ct = default)
    {
        var metadata = await db.SystemMetadata.SingleAsync(x => x.Id == 1, ct);
        metadata.ForceSenderInTitle = force;
        await db.SaveChangesAsync(ct);
        return new SettingsSync { ForceSenderInTitle = force };
    }

    private async Task<long> NextVersionAsync(CancellationToken ct)
    {
        // 事务内原子自增：并发变更各自拿到不同的新版本号，避免读到旧值后各自写同一个 +1 导致丢递增。
        await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE SystemMetadata SET AccountVersion = AccountVersion + 1 WHERE Id = 1;", ct);
            var version = await db.SystemMetadata.Where(x => x.Id == 1)
                .Select(x => x.AccountVersion)
                .SingleAsync(ct);
            await db.Database.CommitTransactionAsync(ct);
            return version;
        }
        catch
        {
            await db.Database.RollbackTransactionAsync(ct);
            throw;
        }
    }

    private async Task<UserProfile> ToProfileAsync(AppUser user, CancellationToken ct)
    {
        var role = user.RoleDefinition ?? await db.AccountRoles.AsNoTracking().SingleAsync(x => x.Id == user.RoleDefinitionId, ct);
        var profile = new UserProfile
        {
            Id = user.Id,
            Username = user.UserName!,
            DisplayName = user.DisplayName,
            Role = user.Role,
            RoleId = role.Id,
            RoleName = role.Name,
            GrantedPermissions = user.GrantedPermissions,
            Permissions = user.Role == UserRole.Admin ? UserPermissions.All : UserPermissions.ViewCurrentCourse | role.DefaultPermissions | user.GrantedPermissions,
            Version = user.Version,
        };
        await extensionPolicies.ApplyAccessAsync(profile, ct);
        return profile;
    }

    private async Task<UserListItem> ToListItemAsync(AppUser user, CancellationToken ct)
    {
        var role = await db.AccountRoles.AsNoTracking().SingleAsync(x => x.Id == user.RoleDefinitionId, ct);
        return new UserListItem
        {
            Id = user.Id,
            Username = user.UserName!,
            DisplayName = user.DisplayName,
            Role = user.Role,
            RoleId = role.Id,
            RoleName = role.Name,
            GrantedPermissions = user.GrantedPermissions,
            EffectivePermissions = user.Role == UserRole.Admin ? UserPermissions.All : UserPermissions.ViewCurrentCourse | role.DefaultPermissions | user.GrantedPermissions,
            Enabled = user.Enabled,
            UpdatedAt = user.UpdatedAt,
        };
    }

    private async Task<AccountRole> ResolveRoleAsync(Guid? roleId, UserRole legacyRole, CancellationToken ct)
    {
        var id = roleId ?? (legacyRole == UserRole.Admin ? AccountRole.AdministratorId : AccountRole.StudentId);
        return await db.AccountRoles.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "Role not found");
    }

    private static UserPermissions NormalizeGrants(UserRole role, UserPermissions grants) => role == UserRole.Admin
        ? UserPermissions.None
        : grants & RolePermissions.Assignable;

    private static string NormalizeDeviceName(string value) => string.IsNullOrWhiteSpace(value)
        ? "Wear OS"
        : value.Trim()[..Math.Min(value.Trim().Length, 80)];

    private static string CreateSecret(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));
    private static string CreateReadableSecret(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedEquals(string left, string right) => CryptographicOperations.FixedTimeEquals(
        Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right));

    private AppUser? _timingUser;

    /// <summary>恒定时间校验用的占位用户；首次校验不存在账号时用真实哈希器生成等成本哈希。</summary>
    private AppUser TimingUser() => _timingUser ??= new AppUser
    {
        UserName = "remoteci-timing-equalizer",
        PasswordHash = users.PasswordHasher.HashPassword(new AppUser(), CreateSecret(16)),
    };

    /// <summary>
    /// 对不存在/已禁用的账号执行一次等成本 PBKDF2 校验，避免响应时间泄露用户名是否存在。
    /// WebUI 登录页与 REST 登录端点共用同一逻辑。
    /// </summary>
    public Task EqualizeLoginTimingAsync(string password) => users.CheckPasswordAsync(TimingUser(), password);

    private static void ValidateUserInput(string username, string displayName, string password, UserRole role)
    {
        if (string.IsNullOrWhiteSpace(username) || !UsernameRegex().IsMatch(username.Trim()))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "ID 需为 3-32 位字母、数字、点、下划线或短横线");
        ValidateDisplayName(displayName);
        ValidatePassword(password);
        ValidateRole(role);
    }

    private static void ValidateDisplayName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 40)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "用户名需为 1-40 个字符");
    }

    private static void ValidatePassword(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length is < 8 or > 128)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "密码需为 8-128 个字符");
    }

    private static void ValidateRole(UserRole role)
    {
        if (role is not UserRole.User and not UserRole.Admin)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "角色无效");
    }

    private static void EnsureIdentitySucceeded(IdentityResult result)
    {
        if (result.Succeeded) return;
        var duplicate = result.Errors.FirstOrDefault(x => x.Code.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
        var message = string.Join("；", result.Errors.Select(x => x.Description));
        throw new IdentityOperationException(duplicate is null ? ApiErrorCodes.InvalidRequest : ApiErrorCodes.UsernameExists, message);
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{3,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();
}

public sealed record AuthPrincipal(
    PeerRole PeerRole,
    UserProfile? User,
    Guid? DeviceSessionId,
    Guid? PluginCredentialId,
    DateTimeOffset? ValidUntil)
{
    public bool IsPlugin => PeerRole == PeerRole.Plugin;
    public bool IsAdmin => User?.Role == UserRole.Admin;
}

public sealed class IdentityOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
