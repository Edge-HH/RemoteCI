using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 把最新账号授权镜像推送给在线插件，并立即刷新所有在线手表的缓存授权。
/// 所有账号、角色和扩展策略变更都通过此入口同步，避免漏掉任一消费者。
/// 授权镜像按班级生成：每个在线插件只收到自己班级的账号与班内角色。
/// </summary>
public sealed class AuthorizationSyncService(IdentityCoordinator identities, PeerRegistry peers)
{
    public async Task SyncAsync(CancellationToken ct = default)
    {
        foreach (var (connectionId, classId) in peers.GetOnlinePluginConnections())
        {
            var sync = await identities.CreateSyncAsync(classId, ct);
            await peers.SendToPluginConnectionAsync(connectionId, Envelope.AccountSync(sync), ct);
        }
        await peers.RefreshWatchAuthorizationsAsync(ct);
    }
}
