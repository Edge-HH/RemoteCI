using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// LanServer 每连接核心判断的纯逻辑，独立于 Fleck 与 ClassIsland 依赖，
/// 供单元测试覆盖挑战认证与权限闸门。LanServer 委托调用，行为不变。
/// </summary>
internal static class LanSessionLogic
{
    /// <summary>校验认证证明请求的形态与时效；通过返回 null，否则返回错误文案。</summary>
    public static string? ValidateAuthProofRequest(Envelope envelope, AuthChallenge? challenge, DateTimeOffset now)
    {
        if (envelope.Type != Protocol.MessageTypeAuthProof || challenge is null)
            return "局域网认证挑战已失效";
        if (challenge.ExpiresAt <= now)
            return "局域网认证挑战已失效";
        return null;
    }

    /// <summary>镜像过期或权限不足时拒绝命令。</summary>
    public static bool CommandDenied(bool mirrorExpired, UserPermissions permissions, UserPermissions required) =>
        mirrorExpired || !permissions.HasFlag(required);

    public static string CommandDeniedMessage(bool mirrorExpired) =>
        mirrorExpired ? "授权镜像超过 24 小时，仅允许查看课程" : "权限不足";

    /// <summary>
    /// 只能经服务端下发的命令：扩展设置需要服务端按“系统管理员或获准的班主任”复核，
    /// 局域网授权镜像不包含班级自治策略，因此局域网直连一律拒绝。
    /// </summary>
    public static bool IsServerOnly(CommandKind command) => command == CommandKind.ApplyExtensionSettings;

    public const string ServerOnlyMessage = "扩展设置只能在 RemoteCI 服务端 WebUI 中修改";
}
