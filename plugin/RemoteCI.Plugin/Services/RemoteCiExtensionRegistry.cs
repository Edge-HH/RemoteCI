using RemoteCI.Plugin.Extensions;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// <see cref="IRemoteCiExtensionRegistry"/> 的线程安全实现。
/// 注册发生在 ClassIsland 插件加载阶段，广播由 RemoteCiService 订阅 ExtensionsChanged / GroupsChanged 完成。
/// 启用扩展接入授权时，每条注册记下注册方插件；未授权插件的注册照常接受（不影响其运行），
/// 但不出现在 GetExtensions / GetGroups 中，因而既不会同步给服务端也不能被远程调用。
/// </summary>
internal sealed class RemoteCiExtensionRegistry : IRemoteCiExtensionRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Owned<IRemoteCiExtension>> _extensions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Owned<IRemoteCiExtensionGroup>> _groups = new(StringComparer.Ordinal);
    private readonly ExtensionAccessService? _access;
    private readonly Func<ExtensionCaller?> _resolveCaller;

    /// <summary>不带授权的注册表：全部注册立即可见（测试与无宿主环境）。</summary>
    public RemoteCiExtensionRegistry()
        : this(null, () => null)
    {
    }

    public RemoteCiExtensionRegistry(ExtensionAccessService? access, Func<ExtensionCaller?> resolveCaller)
    {
        _access = access;
        _resolveCaller = resolveCaller;
        if (_access is not null) _access.Changed += OnAccessChanged;
    }

    public event EventHandler? ExtensionsChanged;

    public event EventHandler? GroupsChanged;

    public IReadOnlyList<IRemoteCiExtension> GetExtensions()
    {
        lock (_lock)
        {
            return _extensions.Values.Where(IsVisible).Select(x => x.Item).ToList();
        }
    }

    public void Register(IRemoteCiExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var id = ExtensionId.Parse(extension.Id, nameof(extension));
        var owner = ResolveOwner();

        lock (_lock)
        {
            if (!_extensions.TryAdd(id.Value, new Owned<IRemoteCiExtension>(extension, owner)))
                throw new InvalidOperationException($"扩展 Id 已存在：{id}");
        }
        RequestAccess(owner);
        ExtensionsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool Unregister(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _extensions.Remove(id);
        }
        if (removed) ExtensionsChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public IReadOnlyList<IRemoteCiExtensionGroup> GetGroups()
    {
        lock (_lock)
        {
            return _groups.Values.Where(IsVisible).Select(x => x.Item).ToList();
        }
    }

    public void RegisterGroup(IRemoteCiExtensionGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var id = ExtensionId.Parse(group.Id, nameof(group));
        ValidateSettingKeys(group);
        var owner = ResolveOwner();

        lock (_lock)
        {
            if (!_groups.TryAdd(id.Value, new Owned<IRemoteCiExtensionGroup>(group, owner)))
                throw new InvalidOperationException($"扩展分组 Id 已存在：{id}");
        }
        RequestAccess(owner);
        GroupsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool UnregisterGroup(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _groups.Remove(id);
        }
        if (removed) GroupsChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public void NotifySettingsChanged(string groupId)
    {
        bool known;
        lock (_lock)
        {
            known = _groups.TryGetValue(groupId, out var group) && IsVisible(group);
        }
        if (known) GroupsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>当前已注册扩展或分组的插件（含未授权的），供设置页展示待授权项。</summary>
    internal IReadOnlyList<ExtensionCaller> GetCallers()
    {
        lock (_lock)
        {
            return _extensions.Values.Select(x => x.Owner)
                .Concat(_groups.Values.Select(x => x.Owner))
                .OfType<ExtensionCaller>()
                .DistinctBy(x => x.PluginId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private ExtensionCaller? ResolveOwner() => _access is null ? null : _resolveCaller();

    private void RequestAccess(ExtensionCaller? owner)
    {
        if (_access is not null && owner is not null) _access.RequestAccess(owner);
    }

    /// <summary>无法识别注册方时（不经 ClassIsland 插件加载的调用）按可信处理，保持旧行为。</summary>
    private bool IsVisible<T>(Owned<T> entry) =>
        _access is null || entry.Owner is null || _access.IsAllowed(entry.Owner.PluginId);

    private void OnAccessChanged()
    {
        ExtensionsChanged?.Invoke(this, EventArgs.Empty);
        GroupsChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed record Owned<T>(T Item, ExtensionCaller? Owner);

    /// <summary>设置字段 Key 是部分更新的唯一定位依据，必须非空且组内唯一，注册时即失败比运行时静默覆盖更易排查。</summary>
    private static void ValidateSettingKeys(IRemoteCiExtensionGroup group)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in group.Settings ?? [])
        {
            if (string.IsNullOrWhiteSpace(field.Key))
                throw new ArgumentException("扩展设置字段 Key 不能为空", nameof(group));
            if (!seen.Add(field.Key))
                throw new ArgumentException($"扩展设置字段 Key 重复：{field.Key}", nameof(group));
        }
    }
}
