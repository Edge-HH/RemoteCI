using System.Diagnostics;
using System.Reflection;
using ClassIsland.Core.Abstractions.Services;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 按调用栈找出调用扩展注册表的 ClassIsland 插件：第一个不属于 RemoteCI 与运行时的栈帧所在程序集，
/// 再按程序集位置对应到已加载插件的目录。扩展接入接口保持不变，第三方插件无需额外声明身份。
/// </summary>
internal static class ExtensionCallerResolver
{
    private static readonly Assembly Own = typeof(ExtensionCallerResolver).Assembly;
    private static readonly Assembly CoreLibrary = typeof(object).Assembly;

    public static ExtensionCaller? Resolve()
    {
        foreach (var frame in new StackTrace(1, false).GetFrames())
        {
            var assembly = frame.GetMethod()?.DeclaringType?.Assembly;
            if (assembly is null || assembly == Own || assembly == CoreLibrary) continue;
            return FromAssembly(assembly);
        }
        return null;
    }

    internal static ExtensionCaller FromAssembly(Assembly assembly)
    {
        var location = SafeLocation(assembly);
        var name = assembly.GetName().Name ?? "未知程序集";
        try
        {
            var plugins = IPluginService.LoadedPlugins.ToList();
            // 优先按程序集所在目录对应插件；从内存加载、没有文件位置时再按清单的入口程序集名对应。
            var match = plugins.FirstOrDefault(plugin => location.Length > 0 && IsUnder(location, plugin.PluginFolderPath)) ??
                plugins.FirstOrDefault(plugin => IsEntranceAssembly(name, plugin.Manifest.EntranceAssembly));
            if (match is not null)
                return new ExtensionCaller(match.Manifest.Id, string.IsNullOrWhiteSpace(match.Manifest.Name)
                    ? match.Manifest.Id
                    : match.Manifest.Name);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TypeLoadException or MissingMemberException) { }
        return new ExtensionCaller("assembly:" + name, name);
    }

    internal static bool IsEntranceAssembly(string assemblyName, string? entranceAssembly) =>
        !string.IsNullOrWhiteSpace(entranceAssembly) &&
        string.Equals(Path.GetFileNameWithoutExtension(entranceAssembly.Trim()), assemblyName, StringComparison.OrdinalIgnoreCase);

    internal static bool IsUnder(string file, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            return Path.GetFullPath(file).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string SafeLocation(Assembly assembly)
    {
        try { return assembly.IsDynamic ? string.Empty : assembly.Location; }
        catch (NotSupportedException) { return string.Empty; }
    }
}
