using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 聊天机器人执行账号/班级管理高危操作前必须经过网页二次授权的动作集合。
/// 控制命令（电源/终端/文件等）与广播、恢复备份不在此列，仍走原有确认流程。
/// </summary>
public static class OperationApprovalActions
{
    public const string CreateUser = "user.create";
    public const string UpdateUser = "user.update";
    public const string ResetPassword = "user.password";
    public const string DeleteUser = "user.delete";
    public const string DeleteClass = "class.delete";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    { CreateUser, UpdateUser, ResetPassword, DeleteUser, DeleteClass };

    public static string Label(string action) => action switch
    {
        CreateUser => "新建账号",
        UpdateUser => "修改账号",
        ResetPassword => "重置登录密码",
        DeleteUser => "删除账号",
        DeleteClass => "删除班级",
        _ => "管理操作",
    };
}

public sealed class OperationApprovalRequest
{
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("targetId")] public Guid TargetId { get; set; }
    /// <summary>新建/修改/改密请求体（与对应 REST 端点同格式）；删除类操作省略。</summary>
    [JsonPropertyName("payload")] public JsonElement? Payload { get; set; }
}

public sealed record OperationApprovalResponse(string Url, string Path, DateTimeOffset ExpiresAt);

/// <summary>一次性授权链接的当前状态，供确认页展示（只含摘要，绝不回显加密参数）。</summary>
public sealed record OperationApprovalState(OperationApproval? Token, string? Problem);

/// <summary>
/// 为聊天机器人签发管理高危操作的一次性网页授权链接。签发时校验调用者权限与目标守卫；
/// 打开链接只展示操作摘要，输入账号密码或一次性验证码（复用二步验证）授权后，服务端原子标记已使用，
/// 再次校验权限并经与 REST 端点相同的服务层执行，审计与授权同步保持一致。
/// 操作参数经 DataProtection 加密存储，页面与日志都不出现明文密码。
/// </summary>
public sealed class OperationApprovalService(
    AppDbContext db,
    IdentityCoordinator identities,
    UserManager<AppUser> users,
    ClassroomService classrooms,
    PeerRegistry peers,
    AuthorizationSyncService authorizationSync,
    TwoFactorService twoFactor,
    IDataProtectionProvider protection,
    TimeProvider timeProvider)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private const int SummaryMaxLength = 400;
    private static readonly JsonSerializerOptions JsonOptions = JsonDefaults.Options;
    private IDataProtector PayloadProtector => protection.CreateProtector("RemoteCI.OperationApproval.Payload.v1");

    public async Task<OperationApprovalResponse> CreateAsync(
        AppUser actor, OperationApprovalRequest request, string baseUrl, CancellationToken ct = default)
    {
        if (!OperationApprovalActions.All.Contains(request.Action)) throw new ArgumentException("不支持的动作。");
        // 签发时即校验权限与目标守卫：无权执行的操作不生成链接。
        await GuardAsync(actor, request.Action, request.TargetId, request.Payload, ct);
        var summary = await BuildSummaryAsync(request.Action, request.TargetId, request.Payload, ct);
        var payloadJson = request.Payload is { ValueKind: JsonValueKind.Object } element ? element.GetRawText() : "";

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = timeProvider.GetUtcNow();
        var expiresAt = now + Lifetime;
        db.OperationApprovals.Add(new OperationApproval
        {
            TokenHash = CalendarFeedService.Hash(token),
            UserId = actor.Id,
            Action = request.Action,
            TargetId = request.TargetId,
            ProtectedPayload = payloadJson.Length == 0 ? "" : PayloadProtector.Protect(payloadJson),
            Summary = summary.Length <= SummaryMaxLength ? summary : summary[..(SummaryMaxLength - 1)] + "…",
            SecurityStamp = actor.SecurityStamp,
            CreatedAtUnixMs = now.ToUnixTimeMilliseconds(),
            ExpiresAtUnixMs = expiresAt.ToUnixTimeMilliseconds(),
        });
        // 顺带清理过期一天以上的链接。
        var stale = now.AddDays(-1).ToUnixTimeMilliseconds();
        await db.OperationApprovals.Where(x => x.ExpiresAtUnixMs < stale).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        var path = $"/Approve/{token}";
        return new OperationApprovalResponse(baseUrl.TrimEnd('/') + path, path, expiresAt);
    }

    public async Task<OperationApprovalState> InspectAsync(string token, CancellationToken ct = default)
    {
        var row = await FindAsync(token, ct);
        if (row is null) return new OperationApprovalState(null, "链接无效或已过期。");
        if (row.UsedAtUnixMs is not null) return new OperationApprovalState(row, "这个链接已经使用过。");
        return new OperationApprovalState(row, Problem(row));
    }

    /// <summary>
    /// 授权执行：先校验账号密码或一次性验证码（失败不消耗链接，可重试），再原子标记已使用
    /// （并发或重复提交只有一次成功），重新校验权限后经服务层执行；执行失败恢复链接以便重试。
    /// </summary>
    public async Task<string> ExecuteAsync(
        string token, string? password, string? code, bool useRecoveryCode, CancellationToken ct = default)
    {
        var row = await FindAsync(token, ct) ?? throw new ArgumentException("链接无效或已过期。");
        if (Problem(row) is { } problem) throw new ArgumentException(problem);
        var actor = await db.Users.Include(x => x.RoleDefinition).SingleOrDefaultAsync(x => x.Id == row.UserId, ct)
            ?? throw new UnauthorizedAccessException("账号不可用。");
        await VerifyStepUpAsync(actor, password, code, useRecoveryCode, ct);

        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var claimed = await db.OperationApprovals
            .Where(x => x.TokenHash == row.TokenHash && x.UsedAtUnixMs == null)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.UsedAtUnixMs, nowMs), ct);
        if (claimed == 0) throw new ArgumentException("这个链接已经使用过。");
        try
        {
            // 执行前重新校验权限与目标守卫：签发后权限或目标状态可能已变化。
            var payload = UnprotectPayload(row);
            await GuardAsync(actor, row.Action, row.TargetId, payload, ct);
            return await RunOperationAsync(actor, row, payload, ct);
        }
        catch (Exception ex) when (ex is IdentityOperationException or ArgumentException or UnauthorizedAccessException)
        {
            await db.OperationApprovals.Where(x => x.TokenHash == row.TokenHash)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.UsedAtUnixMs, (long?)null), CancellationToken.None);
            throw;
        }
    }

    /// <summary>账号密码或一次性验证码二选一授权；复用登录密码校验与二步验证，成功后清零失败计数。</summary>
    private async Task VerifyStepUpAsync(AppUser actor, string? password, string? code, bool useRecoveryCode, CancellationToken ct)
    {
        if (!actor.Enabled) throw new UnauthorizedAccessException("账号不可用。");
        if (!string.IsNullOrWhiteSpace(code))
        {
            if (!actor.TwoFactorEnabled) throw new ArgumentException("该账号未开启二步验证，请使用账号密码授权。");
            if (!await twoFactor.VerifyAsync(actor, code, useRecoveryCode, ct))
                throw new UnauthorizedAccessException("验证码错误、已过期或已使用。");
        }
        else if (!string.IsNullOrWhiteSpace(password))
        {
            try { await identities.CheckLoginPasswordAsync(actor, password); }
            catch (IdentityOperationException ex) { throw new UnauthorizedAccessException(ex.Message); }
        }
        else
        {
            throw new ArgumentException("请输入账号密码或一次性验证码以授权。");
        }
        await users.ResetAccessFailedCountAsync(actor);
    }

    /// <summary>复刻对应 REST 端点的权限与目标守卫（签发与执行各校验一次）。</summary>
    private async Task GuardAsync(AppUser actor, string action, Guid targetId, JsonElement? payload, CancellationToken ct)
    {
        var global = AccountPermissions.Global(actor);
        switch (action)
        {
            case OperationApprovalActions.CreateUser:
            {
                if (!global.HasFlag(UserPermissions.ManageUsers)) throw new UnauthorizedAccessException("没有管理用户的权限。");
                var request = Deserialize<CreateUserRequest>(payload);
                if ((request.Role == UserRole.Admin || request.RoleId == AccountRole.AdministratorId) && actor.Role != UserRole.Admin)
                    throw new UnauthorizedAccessException("只有系统管理员可以创建管理员账号。");
                break;
            }
            case OperationApprovalActions.UpdateUser:
            {
                if (!global.HasFlag(UserPermissions.ManageUsers)) throw new UnauthorizedAccessException("没有管理用户的权限。");
                var request = Deserialize<UpdateUserRequest>(payload);
                if (actor.Role != UserRole.Admin &&
                    ((request.Role == UserRole.Admin || request.RoleId == AccountRole.AdministratorId) ||
                     await identities.GetRoleAsync(targetId, ct) == UserRole.Admin))
                    throw new UnauthorizedAccessException("只有系统管理员可以编辑管理员账号或提升为管理员。");
                await identities.EnsureCanManageAsync(actor.Id, targetId, ct);
                break;
            }
            case OperationApprovalActions.ResetPassword:
            case OperationApprovalActions.DeleteUser:
            {
                if (!global.HasFlag(UserPermissions.ManageUsers)) throw new UnauthorizedAccessException("没有管理用户的权限。");
                if (actor.Role != UserRole.Admin && await identities.GetRoleAsync(targetId, ct) == UserRole.Admin)
                    throw new UnauthorizedAccessException("只有系统管理员可以管理管理员账号。");
                await identities.EnsureCanManageAsync(actor.Id, targetId, ct);
                break;
            }
            case OperationApprovalActions.DeleteClass:
            {
                if (!AccountPermissions.HasManagement(actor, ManagementPermissions.ManageClasses))
                    throw new UnauthorizedAccessException("没有管理班级的权限。");
                break;
            }
            default:
                throw new ArgumentException("不支持的动作。");
        }
    }

    private async Task<string> RunOperationAsync(AppUser actor, OperationApproval row, JsonElement? payload, CancellationToken ct)
    {
        var delegation = new PermissionDelegation(actor.Role, AccountPermissions.Global(actor),
            actor.Role == UserRole.Admin ? ManagementPermissions.All : AccountPermissions.Management(actor));
        switch (row.Action)
        {
            case OperationApprovalActions.CreateUser:
            {
                var created = await identities.CreateUserAsync(Deserialize<CreateUserRequest>(payload), ct, delegation);
                await authorizationSync.SyncAsync(ct);
                return $"已新建账号「{created.DisplayName}」。";
            }
            case OperationApprovalActions.UpdateUser:
            {
                var updated = await identities.UpdateUserAsync(row.TargetId, Deserialize<UpdateUserRequest>(payload), ct, delegation);
                await authorizationSync.SyncAsync(ct);
                return $"已修改账号「{updated.DisplayName}」。";
            }
            case OperationApprovalActions.ResetPassword:
            {
                await identities.ResetPasswordAsync(row.TargetId, Deserialize<ResetPasswordRequest>(payload).Password, ct);
                await authorizationSync.SyncAsync(ct);
                return "已重置该账号的登录密码。";
            }
            case OperationApprovalActions.DeleteUser:
            {
                await identities.DeleteUserAsync(row.TargetId, ct);
                await authorizationSync.SyncAsync(ct);
                return "已删除该账号。";
            }
            case OperationApprovalActions.DeleteClass:
            {
                await classrooms.DeleteAsync(row.TargetId, ct);
                // 班级删除会级联清理插件凭据；对应在线连接立即断开，与 REST 端点一致。
                await peers.DisconnectPluginClassAsync(row.TargetId, ct);
                await authorizationSync.SyncAsync(ct);
                return "已删除该班级及其全部数据。";
            }
            default:
                throw new ArgumentException("不支持的动作。");
        }
    }

    private async Task<string> BuildSummaryAsync(string action, Guid targetId, JsonElement? payload, CancellationToken ct)
    {
        switch (action)
        {
            case OperationApprovalActions.CreateUser:
            {
                var request = Deserialize<CreateUserRequest>(payload);
                var role = request.Role == UserRole.Admin || request.RoleId == AccountRole.AdministratorId ? "系统管理员" : "普通账号";
                return $"新建账号「{request.DisplayName}」（登录 ID：{request.Username}），角色：{role}。";
            }
            case OperationApprovalActions.UpdateUser:
            {
                var request = Deserialize<UpdateUserRequest>(payload);
                return $"修改账号「{await TargetUserNameAsync(targetId, ct)}」：显示名改为「{request.DisplayName}」，状态：{(request.Enabled ? "启用" : "停用")}。";
            }
            case OperationApprovalActions.ResetPassword:
                return $"重置账号「{await TargetUserNameAsync(targetId, ct)}」的登录密码（新密码不会在此显示）。";
            case OperationApprovalActions.DeleteUser:
                return $"删除账号「{await TargetUserNameAsync(targetId, ct)}」，该操作不可撤销。";
            case OperationApprovalActions.DeleteClass:
            {
                var name = await db.Classrooms.AsNoTracking().Where(x => x.Id == targetId).Select(x => x.Name).SingleOrDefaultAsync(ct);
                return $"删除班级「{name ?? targetId.ToString()}」及其课表、成员、插件凭据等全部数据，该操作不可撤销。";
            }
            default:
                return OperationApprovalActions.Label(action);
        }
    }

    private async Task<string> TargetUserNameAsync(Guid targetId, CancellationToken ct)
    {
        var name = await db.Users.AsNoTracking().Where(x => x.Id == targetId).Select(x => x.DisplayName).SingleOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(name) ? targetId.ToString() : name;
    }

    private JsonElement? UnprotectPayload(OperationApproval row)
    {
        if (row.ProtectedPayload.Length == 0) return null;
        return JsonSerializer.Deserialize<JsonElement>(PayloadProtector.Unprotect(row.ProtectedPayload), JsonOptions);
    }

    private static T Deserialize<T>(JsonElement? payload) where T : new()
    {
        if (payload is { ValueKind: JsonValueKind.Object } element)
            return element.Deserialize<T>(JsonOptions) ?? throw new ArgumentException("操作参数无效。");
        throw new ArgumentException("缺少或无效的操作参数。");
    }

    private async Task<OperationApproval?> FindAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return null;
        var hash = CalendarFeedService.Hash(token);
        return await db.OperationApprovals.AsNoTracking().Include(x => x.User).SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
    }

    private string? Problem(OperationApproval row)
    {
        if (row.ExpiresAtUnixMs <= timeProvider.GetUtcNow().ToUnixTimeMilliseconds()) return "链接已过期，请让机器人重新发起。";
        if (!row.User.Enabled || !string.Equals(row.User.SecurityStamp, row.SecurityStamp, StringComparison.Ordinal))
            return "账号状态已变化，链接已失效，请让机器人重新发起。";
        return null;
    }
}