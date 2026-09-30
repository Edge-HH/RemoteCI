using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>集中构造服务端认证成功状态，避免新增协议字段时各发送路径不一致。</summary>
internal static class ServerAuthStateFactory
{
    public static AuthState CreateAuthenticated(UserProfile? user) => new()
    {
        Authenticated = true,
        ServerVersion = AppVersion.Version,
        User = user,
    };
}
