using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Extensions;

/// <summary>
/// RemoteCI 扩展注册表。RemoteCI 插件把它注册到 ClassIsland 主机容器，
/// 其他插件可在 AppStarted 后通过 IAppHost.GetService&lt;IRemoteCiExtensionRegistry&gt;() 获取并注册。
/// </summary>
public interface IRemoteCiExtensionRegistry
{
    /// <summary>当前全部已注册扩展的快照。</summary>
    IReadOnlyList<IRemoteCiExtension> GetExtensions();

    /// <summary>注册一个扩展；Id 已存在时抛出 InvalidOperationException。</summary>
    void Register(IRemoteCiExtension extension);

    /// <summary>按 Id 注销扩展；返回是否成功移除。</summary>
    bool Unregister(string id);

    /// <summary>注册表变化（注册/注销）后触发，RemoteCI 会重新广播扩展清单。</summary>
    event EventHandler? ExtensionsChanged;
}
