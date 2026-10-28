using RemoteCI.Plugin.Extensions;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// <see cref="IRemoteCiExtensionRegistry"/> 的线程安全实现。
/// 注册发生在 ClassIsland 插件加载阶段，广播由 RemoteCiService 订阅 ExtensionsChanged / GroupsChanged 完成。
/// </summary>
internal sealed class RemoteCiExtensionRegistry : IRemoteCiExtensionRegistry
{
    private readonly object _lock = new();
    private readonly Dictionary<string, IRemoteCiExtension> _extensions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IRemoteCiExtensionGroup> _groups = new(StringComparer.Ordinal);

    public event EventHandler? ExtensionsChanged;

    public event EventHandler? GroupsChanged;

    public IReadOnlyList<IRemoteCiExtension> GetExtensions()
    {
        lock (_lock)
        {
            return _extensions.Values.ToList();
        }
    }

    public void Register(IRemoteCiExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var id = ExtensionId.Parse(extension.Id, nameof(extension));

        lock (_lock)
        {
            if (!_extensions.TryAdd(id.Value, extension))
                throw new InvalidOperationException($"扩展 Id 已存在：{id}");
        }
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
            return _groups.Values.ToList();
        }
    }

    public void RegisterGroup(IRemoteCiExtensionGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var id = ExtensionId.Parse(group.Id, nameof(group));
        ValidateSettingKeys(group);

        lock (_lock)
        {
            if (!_groups.TryAdd(id.Value, group))
                throw new InvalidOperationException($"扩展分组 Id 已存在：{id}");
        }
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
            known = _groups.ContainsKey(groupId);
        }
        if (known) GroupsChanged?.Invoke(this, EventArgs.Empty);
    }

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
