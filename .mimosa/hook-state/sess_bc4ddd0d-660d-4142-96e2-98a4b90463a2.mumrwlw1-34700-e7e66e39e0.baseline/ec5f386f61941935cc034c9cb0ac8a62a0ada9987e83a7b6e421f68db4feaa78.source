using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Extensions;

/// <summary>
/// 扩展执行上下文：包含经服务端或局域网挑战认证后的请求者身份，供扩展做审计或附加校验。
/// </summary>
public sealed class ExtensionExecutionContext
{
    /// <summary>发起本次执行的已认证用户（权限已经由 RemoteCI 校验过声明的最小权限）。</summary>
    public required UserProfile RequestedBy { get; init; }

    /// <summary>插件执行端收到命令的时间。</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
