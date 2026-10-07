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

    /// <summary>当前全部已注册扩展分组的快照。</summary>
    IReadOnlyList<IRemoteCiExtensionGroup> GetGroups();

    /// <summary>注册一个扩展分组（及其设置页）；Id 已存在时抛出 InvalidOperationException。</summary>
    void RegisterGroup(IRemoteCiExtensionGroup group);

    /// <summary>按 Id 注销扩展分组；返回是否成功移除。已注册到该分组的扩展会回到“未分组”。</summary>
    bool UnregisterGroup(string id);

    /// <summary>
    /// 通知 RemoteCI 某个分组的设置值在本机发生了变化（例如用户在 ClassIsland 设置界面中修改），
    /// RemoteCI 会重新读取 <see cref="IRemoteCiExtensionGroup.GetSettings"/> 并同步给服务端。
    /// </summary>
    void NotifySettingsChanged(string groupId);

    /// <summary>分组注册、注销或设置值变化后触发，RemoteCI 会重新同步分组与当前设置。</summary>
    event EventHandler? GroupsChanged;
}
