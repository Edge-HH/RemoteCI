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
    ClassAccessService classAccess,
    TeacherBindingService teacherBinding,
    IOptions<ServerOptions> options,
    ILogger<IdentityCoordinator> logger)
{
    private readonly ServerOptions _options = options.Value;

    /// <summary>插件配对码有效期：遗忘在聊天记录/页面上的配对码不能永久可用。</summary>
    private static readonly TimeSpan PairCodeLifetime = TimeSpan.FromMinutes(30);

    /// <summary>首登设置密码令牌有效期：过期后需重新走一次空密码登录获取新令牌。</summary>
    private static readonly TimeSpan PasswordSetupTokenLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// 当前服务端进程实例标识，随授权镜像下发。服务端重启或数据库重建后变化，
    /// 供插件在镜像版本号回退时识别实例变化并强制覆盖，避免旧镜像永久滞留。
    /// </summary>
    public static Guid InstanceId { get; } = Guid.NewGuid();

    /// <summary>管理员角色的变更串行化：保证“最后管理员”检查与提交之间没有其他管理员变更插入。</summary>
    private static readonly SemaphoreSlim AdminMutationGate = new(1, 1);

    /// <summary>每个账号同时保持的设备会话上限，超出时撤销最早创建的会话。</summary>
    private const int MaxActiveSessionsPerUser = 20;

    /// <summary>API Key 的固定前缀，便于与设备访问令牌区分且不改变现有 Bearer 鉴权格式。</summary>
    public const string ApiKeyPrefix = "rci_";

    /// <summary>内置“班主任”角色的默认权限：班内日常管理，不含用户管理、电源/主菜单控制等系统级权限。</summary>
    public const UserPermissions ClassAdministratorDefaultPermissions =
        UserPermissions.ViewCurrentCourse | UserPermissions.AccessWebUi | UserPermissions.ManageSchedule |
        UserPermissions.SendNotifications | UserPermissions.SendVoiceMessages | UserPermissions.TeacherComing |
        UserPermissions.RunExtensions | UserPermissions.ApiAccess | UserPermissions.RequestScheduleSwap;

    /// <summary>
    /// 内置“老师”角色的默认权限：查看当前课程、发送通知与语音消息、发起换课申请，以及用 API Key 读取自己的日程。
    /// 强制换课默认关闭，由系统管理员在角色配置中开启。
    /// 显示名由系统管理员维护。
    /// </summary>
    public const UserPermissions TeacherDefaultPermissions =
        UserPermissions.ViewCurrentCourse | UserPermissions.SendNotifications |
        UserPermissions.SendVoiceMessages | UserPermissions.ApiAccess | UserPermissions.RequestScheduleSwap;

    /// <summary>确保默认班级存在；迁移或首次启动都依赖它承接升级前的全部数据。</summary>
    private async Task SeedDefaultClassroomAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (await db.Classrooms.AnyAsync(x => x.Id == Classroom.DefaultId, ct))
        {
            // 默认班级是迁移前数据的承载对象。它已经存在时只需保留现有记录，
            // 否则管理员自定义的班级名称会在每次服务端启动时被覆盖。
            return;
        }
        db.Classrooms.Add(new Classroom
        {
            Id = Classroom.DefaultId,
            Name = "默认班级",
            VisitorAccessEnabled = false,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task BootstrapAsync(CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);
        // 显式列出全部列：不依赖数据库 schema 中的列默认值，INSERT OR IGNORE 才不会因
        // NOT NULL 约束被静默跳过。
        await db.Database.ExecuteSqlRawAsync(
            "INSERT OR IGNORE INTO SystemMetadata (Id, AccountVersion, ForceSenderInTitle, SchedulePullIntervalMinutes, AutoEnterVisitorPage) VALUES (1, 0, 1, 0, 0);", ct);
        var now = DateTimeOffset.UtcNow;
        if (!await db.AccountRoles.AnyAsync(ct))
        {
            db.AccountRoles.AddRange(
                new AccountRole { Id = AccountRole.StudentId, Name = "Student", NormalizedName = "STUDENT", Kind = AccountRoleKind.Student, DefaultPermissions = UserPermissions.None, CreatedAt = now, UpdatedAt = now },
                new AccountRole { Id = AccountRole.AdministratorId, Name = "Administrator", NormalizedName = "ADMINISTRATOR", Kind = AccountRoleKind.Administrator, DefaultPermissions = UserPermissions.All, CreatedAt = now, UpdatedAt = now },
                new AccountRole { Id = AccountRole.ClassAdministratorId, Name = "ClassAdministrator", NormalizedName = "CLASSADMINISTRATOR", Kind = AccountRoleKind.ClassAdministrator, DefaultPermissions = ClassAdministratorDefaultPermissions, CreatedAt = now, UpdatedAt = now },
                new AccountRole { Id = AccountRole.TeacherId, Name = "Teacher", NormalizedName = "TEACHER", Kind = AccountRoleKind.Teacher, DefaultPermissions = TeacherDefaultPermissions, CreatedAt = now, UpdatedAt = now });
        }
        // 历史迁移 AddRolesAndBackups 会在建库时直接插入两个内置角色，导致上面的 AddRange 被跳过；
        // 班主任角色是新增的，必须独立幂等种子才能同时覆盖全新与已升级的数据库。
        await db.Database.ExecuteSqlRawAsync($"""
            INSERT OR IGNORE INTO AccountRoles (Id, Name, NormalizedName, Kind, DefaultPermissions, CreatedAt, UpdatedAt)
            VALUES ('{AccountRole.ClassAdministratorId}', 'ClassAdministrator', 'CLASSADMINISTRATOR', 4, {(int)ClassAdministratorDefaultPermissions}, '{now:O}', '{now:O}');
            """, ct);
        // 老师角色与班主任同理：独立幂等种子，同时覆盖全新与已升级的数据库。
        await db.Database.ExecuteSqlRawAsync($"""
            INSERT OR IGNORE INTO AccountRoles (Id, Name, NormalizedName, Kind, DefaultPermissions, CreatedAt, UpdatedAt)
            VALUES ('{AccountRole.TeacherId}', 'Teacher', 'TEACHER', 5, {(int)TeacherDefaultPermissions}, '{now:O}', '{now:O}');
            """, ct);
        if (!await db.BackupConfigurations.AnyAsync(ct)) db.BackupConfigurations.Add(new BackupConfiguration());
        await db.SaveChangesAsync(ct);
        await db.AccountRoles.Where(x => x.Id == AccountRole.StudentId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Name, "学生")
            .SetProperty(x => x.NormalizedName, "学生"), ct);
        await db.AccountRoles.Where(x => x.Id == AccountRole.AdministratorId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Name, "管理员")
            .SetProperty(x => x.NormalizedName, "管理员")
            .SetProperty(x => x.DefaultPermissions, UserPermissions.All), ct);
        await db.AccountRoles.Where(x => x.Id == AccountRole.ClassAdministratorId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Name, "班主任")
            .SetProperty(x => x.NormalizedName, "班主任")
            .SetProperty(x => x.Kind, AccountRoleKind.ClassAdministrator), ct);
        await db.AccountRoles.Where(x => x.Id == AccountRole.TeacherId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Name, "老师")
            .SetProperty(x => x.NormalizedName, "老师")
            .SetProperty(x => x.Kind, AccountRoleKind.Teacher), ct);
        await SeedDefaultClassroomAsync(ct);

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
        // 批量导入且未设密码的账号：首次登录（空密码）下发一次性设置令牌，设置密码后重新登录。
        if (user.PasswordPending && string.IsNullOrEmpty(request.Password))
            return await BeginPasswordSetupAsync(user, ct);
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

    /// <summary>为首登账号生成一次性密码设置令牌（15 分钟有效，只保存 SHA-256 摘要）。</summary>
    public async Task<AuthResponse> BeginPasswordSetupAsync(AppUser user, CancellationToken ct = default)
    {
        var token = CreateSecret(32);
        user.SetupTokenHash = Hash(token);
        user.SetupTokenExpiresAt = DateTimeOffset.UtcNow + PasswordSetupTokenLifetime;
        user.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return new AuthResponse { PasswordPending = true, SetupToken = token };
    }

    /// <summary>首登设置密码：校验一次性令牌与有效期，补设密码后账号立即恢复正常登录。</summary>
    public async Task SetupPasswordAsync(SetupPasswordRequest request, CancellationToken ct = default)
    {
        var user = await users.FindByNameAsync(request.Username.Trim());
        var now = DateTimeOffset.UtcNow;
        var invalid = user is null
            || !user.PasswordPending
            || user.SetupTokenHash is null
            || user.SetupTokenExpiresAt is not { } expires
            || expires <= now
            || !FixedEquals(user.SetupTokenHash, Hash(request.SetupToken));
        if (invalid)
        {
            // 恒定时间：对无效请求也执行一次等成本哈希校验，避免枚举可设置密码的账号。
            await users.CheckPasswordAsync(TimingUser(), request.NewPassword);
            throw new IdentityOperationException(ApiErrorCodes.Unauthorized, "设置链接无效或已过期，请重新登录");
        }
        ValidatePassword(request.NewPassword);
        var target = user!;
        EnsureIdentitySucceeded(await users.AddPasswordAsync(target, request.NewPassword));
        target.PasswordPending = false;
        target.SetupTokenHash = null;
        target.SetupTokenExpiresAt = null;
        target.UpdatedAt = now;
        target.Version = await NextVersionAsync(ct);
        EnsureIdentitySucceeded(await users.UpdateAsync(target));
        await users.UpdateSecurityStampAsync(target);
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
            ValidUntil: session.AccessExpiresAt < session.ExpiresAt ? session.AccessExpiresAt : session.ExpiresAt,
            AccessibleClassIds: await classAccess.GetAccessibleClassIdsAsync(session.User.Id, session.User.Role, ct));
    }

    /// <summary>
    /// 校验用户 API Key。密钥只用于识别账号，权限始终从账号当前角色与授权实时计算；
    /// 撤销、过期、账号禁用或失去 ApiAccess 权限后立即失效。
    /// </summary>
    public async Task<AuthPrincipal?> ValidateApiKeyAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(ApiKeyPrefix, StringComparison.Ordinal))
            return null;
        var hash = Hash(token);
        var key = await db.UserApiKeys.Include(x => x.User).ThenInclude(x => x.RoleDefinition)
            .SingleOrDefaultAsync(x => x.KeyHash == hash, ct);
        var now = DateTimeOffset.UtcNow;
        if (key is null || !IsApiKeyActive(key, now))
            return null;

        var profile = await ToProfileAsync(key.User, ct);
        if (!profile.Permissions.HasFlag(UserPermissions.ApiAccess)) return null;

        // API Key 通常由脚本高频调用；与设备会话一样，LastUsedAt 最多每分钟落库一次。
        await TouchApiKeyAsync(key, now, ct);

        return new AuthPrincipal(
            PeerRole.Watch,
            profile,
            DeviceSessionId: null,
            PluginCredentialId: null,
            ValidUntil: key.ExpiresAt,
            AccessibleClassIds: await classAccess.GetAccessibleClassIdsAsync(key.User.Id, key.User.Role, ct),
            ApiKeyId: key.Id);
    }

    private static bool IsApiKeyActive(UserApiKey? key, DateTimeOffset now) =>
        key is not null && key.RevokedAt is null &&
        (key.ExpiresAt is not { } expires || expires > now) && key.User.Enabled;

    private async Task TouchApiKeyAsync(UserApiKey key, DateTimeOffset now, CancellationToken ct)
    {
        if (key.LastUsedAt is not null && now - key.LastUsedAt <= TimeSpan.FromMinutes(1))
            return;
        key.LastUsedAt = now;
        await db.SaveChangesAsync(ct);
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
            ValidUntil: null,
            ClassId: credential.Assigned ? credential.ClassroomId : null);
    }

    public async Task<AuthPrincipal?> ValidateAnyTokenAsync(string token, CancellationToken ct = default) =>
        await ValidatePluginTokenAsync(token, ct) ?? await ValidateAccessTokenAsync(token, ct);

    public async Task<IReadOnlyList<PluginCredentialInfo>> ListPluginCredentialsAsync(CancellationToken ct = default)
    {
        // SQLite 不支持 DateTimeOffset 排序的 SQL 翻译；凭证数量极少，取回后在内存排序。
        var credentials = await db.PluginCredentials.Include(x => x.Classroom).AsNoTracking().ToListAsync(ct);
        return credentials
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new PluginCredentialInfo
            {
                Id = x.Id,
                Name = x.Name,
                ClassName = x.Classroom?.Name,
                ClassNameRemark = x.ClassNameRemark,
                Assigned = x.Assigned,
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
        var candidate = candidates.FirstOrDefault(x =>
                x.ExpiresAt > now && (x.IsShared || x.IsPersistent || x.UsedAt is null))
            ?? throw new IdentityOperationException(ApiErrorCodes.PairCodeInvalid, "插件配对码无效、已使用或已过期");
        if (!candidate.IsShared && !candidate.IsPersistent)
        {
            // 原子消费一次性配对码：并发请求中只有一个能把 UsedAt 从未置位更新为当前时间。
            var consumed = await db.PluginPairingCodes
                .Where(x => x.Id == candidate.Id && x.UsedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UsedAt, now), ct);
            if (consumed == 0)
                throw new IdentityOperationException(ApiErrorCodes.PairCodeInvalid, "插件配对码无效、已使用或已过期");
        }

        var remark = NormalizeClassNameRemark(request.ClassNameRemark);

        // 班级配对码（一次性或固定）直接归属班级；统一连接码创建的设备先进入未分配，
        // 由管理员在班级管理页确认后再绑定班级。
        var token = CreateSecret(32);
        db.PluginCredentials.Add(new PluginCredential
        {
            Id = Guid.NewGuid(),
            Name = "ClassIsland 插件",
            TokenHash = Hash(token),
            ClassroomId = candidate.ClassroomId,
            Assigned = !candidate.IsShared,
            ClassNameRemark = candidate.IsShared ? remark : null,
            CreatedAt = now,
            LastSeenAt = now,
            Enabled = true,
        });
        await db.SaveChangesAsync(ct);
        return new PairResponse { Token = token, Role = "plugin", ExpiresAt = null };
    }

    /// <summary>生成绑定班级的一次性配对码；插件使用后立即失效。</summary>
    public async Task<string> CreatePluginPairingCodeAsync(Guid classroomId, CancellationToken ct = default)
    {
        if (!await db.Classrooms.AnyAsync(x => x.Id == classroomId, ct))
            throw new IdentityOperationException(ApiErrorCodes.NotFound, "班级不存在");
        var code = CreateReadableSecret(12);
        await AddPairingCodeAsync(code, ct, classroomId: classroomId);
        return code;
    }

    /// <summary>
    /// 设置班级固定配对码：可重复使用，插件用该码连接后自动绑定到本班。
    /// 同一班级只保留一个固定码，重复设置会替换旧码；留空则自动生成。
    /// </summary>
    public async Task<string> SetClassPairingCodeAsync(Guid classroomId, string? requestedCode, CancellationToken ct = default)
    {
        if (!await db.Classrooms.AnyAsync(x => x.Id == classroomId, ct))
            throw new IdentityOperationException(ApiErrorCodes.NotFound, "班级不存在");
        var code = NormalizePairingCode(requestedCode) ?? CreateReadableSecret(12);
        await db.PluginPairingCodes
            .Where(x => x.ClassroomId == classroomId && x.IsPersistent)
            .ExecuteDeleteAsync(ct);
        await AddPairingCodeAsync(code, ct, classroomId: classroomId, timeLimited: false, isPersistent: true);
        return code;
    }

    /// <summary>创建可重复使用的统一连接码；使用该码生成的设备进入未分配列表。</summary>
    public async Task<string> CreateSharedPluginPairingCodeAsync(string? requestedCode = null, CancellationToken ct = default)
    {
        var code = NormalizePairingCode(requestedCode) ?? CreateReadableSecret(12);
        await db.PluginPairingCodes.Where(x => x.IsShared).ExecuteDeleteAsync(ct);
        await AddPairingCodeAsync(code, ct, classroomId: Classroom.DefaultId, timeLimited: false, isShared: true);
        return code;
    }

    /// <summary>是否已设置统一连接码；用于班级管理页显示当前状态（不返回明文）。</summary>
    public async Task<bool> HasSharedPluginPairingCodeAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var expiries = await db.PluginPairingCodes
            .Where(x => x.IsShared)
            .Select(x => x.ExpiresAt)
            .ToListAsync(ct);
        return expiries.Any(expiresAt => expiresAt > now);
    }

    /// <summary>未分配设备：使用统一连接码接入、尚未绑定班级的插件凭据。</summary>
    public async Task<IReadOnlyList<PluginCredentialInfo>> ListUnassignedPluginCredentialsAsync(CancellationToken ct = default)
    {
        var credentials = await db.PluginCredentials.AsNoTracking()
            .Where(x => !x.Assigned)
            .ToListAsync(ct);
        return credentials
            .OrderByDescending(x => x.LastSeenAt)
            .Select(x => new PluginCredentialInfo
            {
                Id = x.Id,
                Name = x.Name,
                ClassName = null,
                ClassNameRemark = x.ClassNameRemark,
                Assigned = false,
                CreatedAt = x.CreatedAt,
                LastSeenAt = x.LastSeenAt,
                Enabled = x.Enabled,
            }).ToList();
    }

    /// <summary>把未分配设备绑定到班级；绑定后由调用方刷新在线连接并推送新的授权镜像。</summary>
    public async Task AssignPluginCredentialAsync(Guid credentialId, Guid classroomId, CancellationToken ct = default)
    {
        if (!await db.Classrooms.AnyAsync(x => x.Id == classroomId, ct))
            throw new IdentityOperationException(ApiErrorCodes.NotFound, "班级不存在");
        var credential = await db.PluginCredentials.SingleOrDefaultAsync(x => x.Id == credentialId, ct)
            ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "插件凭据不存在");
        credential.ClassroomId = classroomId;
        credential.Assigned = true;
        await db.SaveChangesAsync(ct);
    }

    public async Task<UserProfile?> GetProfileAsync(Guid id, CancellationToken ct = default)
    {
        var user = await users.FindByIdAsync(id.ToString());
        return user is null || !user.Enabled ? null : await ToProfileAsync(user, ct);
    }

    /// <summary>创建用户 API Key；调用方必须已经完成目标账号管理或本人权限校验。</summary>
    public async Task<ApiKeyCreationResult> CreateApiKeyAsync(
        Guid userId, string? name, CancellationToken ct = default)
    {
        var user = await RequireUserAsync(userId);
        if (!user.Enabled)
            throw new IdentityOperationException(ApiErrorCodes.Forbidden, "账号已禁用，不能创建 API Key");
        var profile = await ToProfileAsync(user, ct);
        if (!profile.Permissions.HasFlag(UserPermissions.ApiAccess))
            throw new IdentityOperationException(ApiErrorCodes.Forbidden, "账号没有 API 访问权限");

        var key = CreateApiKeySecret();
        var now = DateTimeOffset.UtcNow;
        var entity = new UserApiKey
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Name = NormalizeApiKeyName(name),
            KeyHash = Hash(key),
            Prefix = key[..Math.Min(12, key.Length)],
            CreatedAt = now,
        };
        db.UserApiKeys.Add(entity);
        await db.SaveChangesAsync(ct);
        return new ApiKeyCreationResult
        {
            UserId = user.Id,
            Username = user.UserName ?? string.Empty,
            ApiKey = ToApiKeyInfo(entity),
            Key = key,
        };
    }

    /// <summary>列出指定账号的 API Key 管理信息，不返回任何明文或摘要。</summary>
    public async Task<IReadOnlyList<ApiKeyInfo>> ListApiKeysAsync(Guid userId, CancellationToken ct = default) =>
        (await db.UserApiKeys.AsNoTracking().Where(x => x.UserId == userId).ToListAsync(ct))
            .OrderByDescending(x => x.CreatedAt)
            .Select(ToApiKeyInfo)
            .ToList();

    /// <summary>批量读取多个账号的 API Key，供人员管理页一次性渲染，避免逐账号查询。</summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ApiKeyInfo>>> ListApiKeysByUsersAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<ApiKeyInfo>>();
        var keys = await db.UserApiKeys.AsNoTracking()
            .Where(x => userIds.Contains(x.UserId))
            .ToListAsync(ct);
        return keys
            .GroupBy(x => x.UserId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<ApiKeyInfo>)group
                    .OrderByDescending(x => x.CreatedAt)
                    .Select(ToApiKeyInfo)
                    .ToList());
    }

    /// <summary>吊销指定账号的 API Key；重复吊销保持幂等。</summary>
    public async Task RevokeApiKeyAsync(Guid userId, Guid keyId, CancellationToken ct = default)
    {
        var key = await db.UserApiKeys.SingleOrDefaultAsync(x => x.Id == keyId && x.UserId == userId, ct)
            ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, "API Key 不存在");
        if (key.RevokedAt is not null) return;
        key.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static ApiKeyInfo ToApiKeyInfo(UserApiKey key) => new()
    {
        Id = key.Id,
        Name = key.Name,
        Prefix = key.Prefix,
        CreatedAt = key.CreatedAt,
        LastUsedAt = key.LastUsedAt,
        ExpiresAt = key.ExpiresAt,
        RevokedAt = key.RevokedAt,
    };

    private static string NormalizeApiKeyName(string? value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "默认密钥" : value.Trim();
        if (name.Length > 40)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "API Key 名称不能超过 40 个字符");
        return name;
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
        // 密码留空 = 批量导入待激活账号：首次登录时强制设置密码。
        var hasPassword = !string.IsNullOrWhiteSpace(request.Password);
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = request.Username.Trim(),
            DisplayName = request.DisplayName.Trim(),
            Role = protocolRole,
            RoleDefinitionId = role.Id,
            GrantedPermissions = NormalizeGrants(protocolRole, request.GrantedPermissions),
            Enabled = true,
            PasswordPending = !hasPassword,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        user.Version = await NextVersionAsync(ct);
        EnsureIdentitySucceeded(hasPassword
            ? await users.CreateAsync(user, request.Password)
            : await users.CreateAsync(user));
        db.ClassMemberships.Add(new ClassMembership
        {
            UserId = user.Id,
            ClassroomId = Classroom.DefaultId,
            RoleDefinitionId = role.Id,
        });
        await db.SaveChangesAsync(ct);
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
            // Users 页编辑的是账号的全局默认角色；默认班级成员关系跟随它，其他班级成员角色独立管理。
            var defaultMembership = await db.ClassMemberships.SingleOrDefaultAsync(
                x => x.UserId == user.Id && x.ClassroomId == Classroom.DefaultId, ct);
            if (defaultMembership is not null) defaultMembership.RoleDefinitionId = targetRole.Id;
            await db.SaveChangesAsync(ct);
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

    /// <summary>系统管理员修改用户可见用户名（DisplayName）；登录 ID 与设备会话保持不变，仅同步账号版本。</summary>
    public async Task ChangeDisplayNameAsync(Guid id, ChangeDisplayNameRequest request, CancellationToken ct = default)
    {
        ValidateDisplayName(request.DisplayName);
        var user = await RequireUserAsync(id);
        if (user.Role != UserRole.Admin)
            throw new IdentityOperationException(ApiErrorCodes.Forbidden, "仅系统管理员可以修改用户名。");
        user.DisplayName = request.DisplayName.Trim();
        user.UpdatedAt = DateTimeOffset.UtcNow;
        user.Version = await NextVersionAsync(ct);
        EnsureIdentitySucceeded(await users.UpdateAsync(user));
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

    /// <summary>
    /// 生成面向指定班级插件的授权镜像：账号为该班成员 ∪ 启用的系统管理员，
    /// 有效权限按该班成员角色计算；插件局域网认证只需要能连到自己的账号。
    /// </summary>
    public async Task<AccountSync> CreateSyncAsync(Guid classId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var accountVersion = await db.SystemMetadata.Where(x => x.Id == 1).Select(x => x.AccountVersion).SingleAsync(ct);

        // 班级成员关系一次性取回，再与启用的系统管理员合并。
        var memberships = await db.ClassMemberships.AsNoTracking()
            .Where(x => x.ClassroomId == classId)
            .Select(x => new { x.UserId, x.RoleDefinitionId, x.RoleDefinition.DefaultPermissions })
            .ToListAsync(ct);
        var memberIds = memberships.Select(x => x.UserId).ToHashSet();
        var memberRoles = memberships.ToDictionary(x => x.UserId, x => (x.RoleDefinitionId, x.DefaultPermissions));

        // 按显示名绑定本班课表的“老师”账号：即使不是班级成员也进入授权镜像，
        // 否则 LAN 直连时插件端会因其不在镜像中而拒绝老师发送的通知与语音消息。
        var taughtTeacherIds = (await teacherBinding.GetMatchedTeacherUserIdsAsync(classId, ct)).ToList();
        var accounts = await users.Users.Include(x => x.RoleDefinition)
            .Where(x => x.Enabled && (x.Role == UserRole.Admin || memberIds.Contains(x.Id) || taughtTeacherIds.Contains(x.Id)))
            .ToListAsync(ct);
        var roleIds = accounts.Select(x => x.RoleDefinitionId)
            .Concat(memberRoles.Values.Select(x => x.RoleDefinitionId))
            .Distinct().ToList();
        var roleNames = await db.AccountRoles.AsNoTracking()
            .Where(x => roleIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        var extensionPolicyRows = await db.ExtensionPolicies.AsNoTracking()
            .Where(x => x.Enabled).ToListAsync(ct);
        var hiddenByUser = (await db.UserExtensionPreferences.AsNoTracking()
                .Where(x => !x.ShowOnWatch)
                .Select(x => new { x.UserId, x.ExtensionId })
                .ToListAsync(ct))
            .GroupBy(x => x.UserId)
            .ToDictionary(group => group.Key, group => (IReadOnlyCollection<string>)group.Select(x => x.ExtensionId).ToArray());

        var syncedAccounts = new List<SyncedAccount>();
        foreach (var user in accounts)
        {
            var isAdmin = user.Role == UserRole.Admin;
            var (memberRoleId, memberDefaults) = memberRoles.TryGetValue(user.Id, out var membership)
                ? membership
                : (user.RoleDefinitionId, user.RoleDefinition?.DefaultPermissions ?? UserPermissions.None);
            var effective = isAdmin
                ? UserPermissions.All
                : UserPermissions.ViewCurrentCourse | memberDefaults | user.GrantedPermissions;
            var account = new SyncedAccount
            {
                Id = user.Id,
                Username = user.UserName!,
                DisplayName = user.DisplayName,
                Role = user.Role,
                RoleId = isAdmin ? user.RoleDefinitionId : memberRoleId,
                RoleName = roleNames.GetValueOrDefault(isAdmin ? user.RoleDefinitionId : memberRoleId),
                GrantedPermissions = user.GrantedPermissions,
                EffectivePermissions = effective,
                Enabled = user.Enabled,
                Version = user.Version,
            };
            var profile = account.ToProfile();
            ExtensionPolicyService.ApplyAccess(
                profile,
                extensionPolicyRows,
                hiddenByUser.GetValueOrDefault(account.Id, Array.Empty<string>()));
            account.AllowedExtensionIds = profile.AllowedExtensionIds;
            account.VisibleExtensionIds = profile.VisibleExtensionIds;
            syncedAccounts.Add(account);
        }

        var accountIds = syncedAccounts.Select(x => x.Id).ToHashSet();
        var sessions = (await db.DeviceSessions.AsNoTracking()
                .Where(x => x.RevokedAt == null).ToListAsync(ct))
            .Where(x => x.ExpiresAt > now && accountIds.Contains(x.UserId))
            .Select(x => new SyncedDeviceSession
            {
                Id = x.Id,
                UserId = x.UserId,
                Verifier = x.VerifierHash,
                ExpiresAt = x.ExpiresAt,
            }).ToList();
        var className = await db.Classrooms.AsNoTracking()
            .Where(x => x.Id == classId)
            .Select(x => x.Name)
            .SingleOrDefaultAsync(ct);
        return new AccountSync
        {
            Version = accountVersion,
            ServerInstanceId = InstanceId,
            ServerVersion = AppVersion.Version,
            ServerCapabilities = RemoteCiCapabilities.Current.ToList(),
            GeneratedAt = now,
            ClassName = className,
            Accounts = syncedAccounts,
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

    private async Task AddPairingCodeAsync(
        string code, CancellationToken ct, bool timeLimited = true, Guid? classroomId = null,
        bool isShared = false, bool isPersistent = false)
    {
        var now = DateTimeOffset.UtcNow;
        var codeHash = Hash(code);
        // SQLite stores DateTimeOffset as TEXT and cannot translate this comparison reliably.
        var duplicateExpiries = await db.PluginPairingCodes
            .Where(x => x.CodeHash == codeHash)
            .Select(x => x.ExpiresAt)
            .ToListAsync(ct);
        if (duplicateExpiries.Any(expiresAt => expiresAt > now))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "插件配对码已存在，请换一个值。");
        db.PluginPairingCodes.Add(new PluginPairingCode
        {
            Id = Guid.NewGuid(),
            CodeHash = codeHash,
            ClassroomId = classroomId ?? Classroom.DefaultId,
            IsShared = isShared,
            IsPersistent = isPersistent,
            CreatedAt = now,
            // WebUI 生成的配对码限时 30 分钟；一次性消费仍由 UsedAt 原子控制。
            ExpiresAt = timeLimited ? now.Add(PairCodeLifetime) : DateTimeOffset.MaxValue,
        });
        await db.SaveChangesAsync(ct);
    }

    private static string? NormalizePairingCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length is < 6 or > 64)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "插件配对码需为 6-64 个字符。");
        if (normalized.Any(char.IsWhiteSpace))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "插件配对码不能包含空白字符。");
        return normalized;
    }

    private static string? NormalizeClassNameRemark(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        return normalized.Length > 40 ? normalized[..40] : normalized;
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
            RoleKind = (int)role.Kind,
            GrantedPermissions = user.GrantedPermissions,
            Permissions = user.Role == UserRole.Admin ? UserPermissions.All : UserPermissions.ViewCurrentCourse | role.DefaultPermissions | user.GrantedPermissions,
            Classes = [.. await classAccess.GetAccessibleClassesAsync(user.Id, user.Role, user.GrantedPermissions, ct)],
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
        : grants & (RolePermissions.Assignable & ~UserPermissions.ChangeDisplayName);

    private static string NormalizeDeviceName(string value) => string.IsNullOrWhiteSpace(value)
        ? "Wear OS"
        : value.Trim()[..Math.Min(value.Trim().Length, 80)];

    private static string CreateSecret(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes));
    /// <summary>生成 URL 安全的 API Key，固定带前缀以便服务端快速区分设备访问令牌。</summary>
    private static string CreateApiKeySecret() =>
        ApiKeyPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
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
        // 密码留空表示批量导入待激活账号（首次登录强制设置密码）。
        if (!string.IsNullOrWhiteSpace(password)) ValidatePassword(password);
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

/// <summary>
/// 已认证连接/请求的主体。插件连接带其归属班级 ClassId；
/// 用户连接带可访问班级列表 AccessibleClassIds（管理员为全部班级快照，由刷新任务周期更新）。
/// </summary>
public sealed record AuthPrincipal(
    PeerRole PeerRole,
    UserProfile? User,
    Guid? DeviceSessionId,
    Guid? PluginCredentialId,
    DateTimeOffset? ValidUntil,
    Guid? ClassId = null,
    IReadOnlyList<Guid>? AccessibleClassIds = null,
    Guid? ApiKeyId = null)
{
    public bool IsPlugin => PeerRole == PeerRole.Plugin;
    public bool IsApiKey => ApiKeyId is not null;
    public bool IsAdmin => User?.Role == UserRole.Admin;

    /// <summary>该主体的连接是否覆盖指定班级（插件看归属班，用户看成员/管理范围）。</summary>
    public bool CoversClass(Guid classId) => IsPlugin
        ? ClassId == classId
        : IsAdmin || (AccessibleClassIds?.Contains(classId) ?? false);
}

public sealed class IdentityOperationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
