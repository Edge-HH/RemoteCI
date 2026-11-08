using System.Collections.ObjectModel;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Services.Registry;
using Microsoft.Extensions.Logging;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 执行班级策略的“禁止插件设置”：把 ClassIsland 自带的“插件”页从设置页注册表中移除，解除后放回原位。
/// ClassIsland 集控策略没有这一项，宿主设置窗口按 <see cref="SettingsWindowRegistryService.Registered"/>
/// 生成导航菜单并解析 classisland://app/settings/{id}，移除后菜单不显示、URI 也打不开该页。
/// </summary>
public sealed class PluginsSettingsPageGuard(ILogger<PluginsSettingsPageGuard>? logger = null)
{
    /// <summary>ClassIsland 自带插件页的 ID（PluginsSettingsPage）。</summary>
    internal const string PluginsPageId = "classisland.plugins";

    private readonly ObservableCollection<SettingsPageInfo>? _registry;
    private SettingsPageInfo? _hidden;
    private int _hiddenIndex;

    /// <summary>测试用：使用独立的注册表，不碰宿主的全局集合。</summary>
    internal PluginsSettingsPageGuard(ObservableCollection<SettingsPageInfo> registry) : this((ILogger<PluginsSettingsPageGuard>?)null)
    {
        _registry = registry;
    }

    private ObservableCollection<SettingsPageInfo> Registry => _registry ?? SettingsWindowRegistryService.Registered;

    public bool IsHidden => _hidden is not null;

    /// <summary>按策略隐藏或恢复插件页；必须在 UI 线程调用。返回注册表是否发生变化。</summary>
    public bool Apply(bool hide)
    {
        var registry = Registry;
        bool changed;
        if (hide)
        {
            var index = IndexOf(registry);
            changed = index >= 0;
            if (changed)
            {
                _hidden = registry[index];
                _hiddenIndex = index;
                registry.RemoveAt(index);
            }
        }
        else
        {
            changed = _hidden is not null && IndexOf(registry) < 0;
            if (changed)
                registry.Insert(Math.Clamp(_hiddenIndex, 0, registry.Count), _hidden!);
            _hidden = null;
        }

        if (changed && _registry is null) RefreshOpenSettingsWindows();
        return changed;
    }

    private static int IndexOf(IList<SettingsPageInfo> registry)
    {
        for (var i = 0; i < registry.Count; i++)
            if (registry[i].Id == PluginsPageId) return i;
        return -1;
    }

    /// <summary>
    /// 宿主设置窗口是单例，导航菜单只在构造时（及切换调试选项时）生成；已创建的窗口需要重建菜单，
    /// 否则旧菜单项仍能点进插件页。宿主未公开该方法，按名称反射调用，找不到时只记录日志。
    /// </summary>
    private void RefreshOpenSettingsWindows()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        foreach (var window in desktop.Windows)
        {
            var type = window.GetType();
            if (type.FullName != "ClassIsland.Views.SettingsWindowNew") continue;
            try
            {
                var rebuild = type.GetMethod("BuildNavigationMenuItems", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (rebuild is null)
                {
                    logger?.LogWarning("当前 ClassIsland 版本的设置窗口无法刷新导航菜单，插件页入口将在重启后更新");
                    continue;
                }
                rebuild.Invoke(window, null);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "刷新 ClassIsland 设置窗口导航菜单失败");
            }
        }
    }
}
