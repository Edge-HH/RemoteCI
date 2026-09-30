using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class PluginCompatibilityTests
{
    [Fact]
    public void EntranceAssemblyCanEnumerateExportedTypesOnClassIsland2Baseline()
    {
        // ClassIsland 的插件加载器会调用同一个 API 扫描入口类型；版本不匹配会在这里直接复现。
        var exportedTypes = typeof(global::RemoteCI.Plugin.Plugin).Assembly.GetExportedTypes();

        Assert.Contains(typeof(global::RemoteCI.Plugin.Plugin), exportedTypes);
    }
}
