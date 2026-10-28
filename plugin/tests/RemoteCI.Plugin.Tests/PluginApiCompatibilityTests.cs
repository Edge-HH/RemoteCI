using System.Reflection;
using Mono.Cecil;
using Xunit;

namespace RemoteCI.Plugin.Tests;

/// <summary>
/// 二进制兼容检查：插件以 ClassIsland 2.1.0 SDK 编译，但要在更新的宿主中运行。宿主改了公开成员的
/// 返回类型或参数类型时，源码照常编译，运行时才抛 MissingMethodException（例如 2.1.1 的
/// <c>AppBase.GetRootWindow()</c>）。这里逐个解析插件引用的宿主类型与成员，确认在每个支持的宿主中都存在。
/// </summary>
public sealed class PluginApiCompatibilityTests
{
    /// <summary>由宿主提供、插件包不携带的程序集；插件引用了其中的类型或成员就必须逐个核对。</summary>
    private static readonly string[] HostAssemblies =
    [
        "ClassIsland.Core", "ClassIsland.Shared", "ClassIsland.Shared.IPC", "ClassIsland.Platforms.Abstractions",
        "Avalonia.Base", "Avalonia.Controls", "Avalonia.Markup.Xaml", "FluentAvalonia", "CommunityToolkit.Mvvm", "0Harmony",
    ];

    /// <summary>ClassIsland 2.1.1.1（.NET 10）随附的程序集，对应测试项目里的 PackageDownload。</summary>
    private static readonly (string Package, string Version)[] Host2111Packages =
    [
        ("classisland.core", "2.1.1.1"), ("classisland.shared", "2.1.1.1"), ("classisland.shared.ipc", "2.1.1.1"),
        ("classisland.platforms.abstractions", "2.1.1.1"), ("avalonia", "12.1.1"), ("fluentavaloniaui", "3.0.0"),
        ("communitytoolkit.mvvm", "8.2.1"), ("lib.harmony.thin", "2.4.2"),
    ];

    [Fact]
    public void PluginReferencesResolveOnMinimumHost_2_1_0_1()
    {
        // 测试输出目录里的宿主程序集来自插件编译所用的 SDK 2.1.0.1 依赖图。
        AssertResolves(AppContext.BaseDirectory);
    }

    [Fact]
    public void PluginReferencesResolveOnHost_2_1_1_1()
    {
        var packageRoot = typeof(PluginApiCompatibilityTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == "NuGetPackageRoot").Value!;
        var hostDirectory = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", "host-2.1.1.1");
        Directory.CreateDirectory(hostDirectory);
        foreach (var (package, version) in Host2111Packages)
        {
            var lib = Path.Combine(packageRoot, package, version, "lib");
            var framework = new[] { "net10.0", "net9.0", "net8.0", "netstandard2.1", "netstandard2.0" }
                .Select(x => Path.Combine(lib, x))
                .FirstOrDefault(Directory.Exists)
                ?? throw new DirectoryNotFoundException($"{package} {version} 未还原，请先 dotnet restore 测试项目");
            foreach (var dll in Directory.GetFiles(framework, "*.dll"))
                File.Copy(dll, Path.Combine(hostDirectory, Path.GetFileName(dll)), true);
        }

        AssertResolves(hostDirectory);
    }

    private static void AssertResolves(string hostDirectory)
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var directory in resolver.GetSearchDirectories()) resolver.RemoveSearchDirectory(directory);
        resolver.AddSearchDirectory(hostDirectory);
        using var plugin = ModuleDefinition.ReadModule(typeof(HostApiCompat).Assembly.Location,
            new ReaderParameters { AssemblyResolver = resolver });

        bool FromHost(TypeReference type) => HostAssemblies.Contains(type.GetElementType().Scope.Name);
        var checkedScopes = new HashSet<string>();
        var unresolved = new List<string>();
        foreach (var type in plugin.GetTypeReferences().Where(FromHost))
        {
            checkedScopes.Add(type.Scope.Name);
            if (TryResolve(type.Resolve) is null) unresolved.Add($"类型 {type.FullName}");
        }
        foreach (var member in plugin.GetMemberReferences().Where(x => FromHost(x.DeclaringType)))
        {
            IMemberDefinition? resolved = member switch
            {
                MethodReference method => TryResolve(method.Resolve),
                FieldReference field => TryResolve(field.Resolve),
                _ => null,
            };
            if (resolved is null) unresolved.Add($"成员 {member.FullName}");
        }

        // 防止宿主程序集缺失时检查变成空跑。
        Assert.Contains("ClassIsland.Core", checkedScopes);
        Assert.Contains("Avalonia.Controls", checkedScopes);
        Assert.True(unresolved.Count == 0,
            $"插件在 {hostDirectory} 的宿主中有 {unresolved.Count} 个引用无法解析，请改为经 HostApiCompat 按名称访问：{Environment.NewLine}" +
            string.Join(Environment.NewLine, unresolved));
    }

    private static T? TryResolve<T>(Func<T> resolve) where T : class
    {
        try { return resolve(); }
        catch (AssemblyResolutionException) { return null; }
    }
}
