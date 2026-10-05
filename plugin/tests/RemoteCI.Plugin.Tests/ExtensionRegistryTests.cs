using RemoteCI.Plugin.Extensions;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ExtensionRegistryTests
{
    [Fact]
    public void RegisterAndUnregister_RaiseChangeEventsAndMutateSnapshot()
    {
        var registry = new RemoteCiExtensionRegistry();
        var changed = 0;
        registry.ExtensionsChanged += (_, _) => changed++;

        registry.Register(new FakeExtension("demo.a", "扩展 A"));
        Assert.Equal(1, changed);
        Assert.Equal("demo.a", Assert.Single(registry.GetExtensions()).Id);

        Assert.True(registry.Unregister("demo.a"));
        Assert.Equal(2, changed);
        Assert.Empty(registry.GetExtensions());
        Assert.False(registry.Unregister("demo.a"));
    }

    [Fact]
    public void RegisterDuplicateId_ThrowsWithoutTouchingRegistry()
    {
        var registry = new RemoteCiExtensionRegistry();
        registry.Register(new FakeExtension("demo.a", "扩展 A"));

        Assert.Throws<InvalidOperationException>(
            () => registry.Register(new FakeExtension("demo.a", "扩展 A 重复")));
        Assert.Single(registry.GetExtensions());
    }

    [Fact]
    public void RegisterBlankId_Throws()
    {
        var registry = new RemoteCiExtensionRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(new FakeExtension(" ", "空白 Id")));
    }

    [Theory]
    [InlineData(" demo")]
    [InlineData("demo ")]
    public void RegisterIdWithOuterWhitespace_Throws(string id)
    {
        var registry = new RemoteCiExtensionRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(new FakeExtension(id, "非法 Id")));
    }

    [Fact]
    public void RegisterOverlongId_Throws()
    {
        var registry = new RemoteCiExtensionRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(
            new FakeExtension(new string('x', ExtensionId.MaxLength + 1), "超长 Id")));
    }

    [Fact]
    public void Groups_RegisterNotifyAndUnregister_RaiseGroupsChangedOnly()
    {
        var registry = new RemoteCiExtensionRegistry();
        var groupsChanged = 0;
        var extensionsChanged = 0;
        registry.GroupsChanged += (_, _) => groupsChanged++;
        registry.ExtensionsChanged += (_, _) => extensionsChanged++;

        registry.RegisterGroup(new FakeGroup("demo.group", "演示插件"));
        registry.NotifySettingsChanged("demo.group");
        registry.NotifySettingsChanged("unknown.group");
        Assert.True(registry.UnregisterGroup("demo.group"));
        Assert.False(registry.UnregisterGroup("demo.group"));

        Assert.Equal(3, groupsChanged);
        Assert.Equal(0, extensionsChanged);
        Assert.Empty(registry.GetGroups());
    }

    [Fact]
    public void RegisterGroup_RejectsDuplicateIdAndDuplicateSettingKeys()
    {
        var registry = new RemoteCiExtensionRegistry();
        registry.RegisterGroup(new FakeGroup("demo.group", "演示插件"));

        Assert.Throws<InvalidOperationException>(() => registry.RegisterGroup(new FakeGroup("demo.group", "重复")));
        Assert.Throws<ArgumentException>(() => registry.RegisterGroup(new FakeGroup("demo.other", "重复字段",
        [
            new ExtensionParameter { Key = "volume", Label = "音量" },
            new ExtensionParameter { Key = "volume", Label = "音量（重复）" },
        ])));
        Assert.Single(registry.GetGroups());
    }

    [Fact]
    public void ExtensionWithoutNewMembers_UsesDefaultInterfaceImplementations()
    {
        IRemoteCiExtension extension = new FakeExtension("demo.legacy", "旧扩展");

        Assert.Null(extension.Description);
        Assert.Null(extension.GroupId);
    }

    /// <summary>测试用扩展分组：保存最近一次应用的设置，供断言部分更新语义。</summary>
    internal sealed class FakeGroup(
        string id,
        string displayName,
        IReadOnlyList<ExtensionParameter>? settings = null)
        : RemoteCiExtensionGroupBase
    {
        public override string Id { get; } = id;
        public override string DisplayName { get; } = displayName;
        public override IReadOnlyList<ExtensionParameter> Settings { get; } = settings ?? [];
        public Dictionary<string, string?> Current { get; } = new(StringComparer.Ordinal);

        public override IReadOnlyDictionary<string, string?> GetSettings() => Current;

        public override Task<CommandResult> ApplySettingsAsync(
            ExtensionExecutionContext context,
            IReadOnlyDictionary<string, string?> values,
            CancellationToken cancellationToken)
        {
            foreach (var pair in values) Current[pair.Key] = pair.Value;
            return Task.FromResult(new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "设置已保存" });
        }
    }

    /// <summary>测试用扩展：默认成功返回，可注入参数与执行回调。</summary>
    internal sealed class FakeExtension(
        string id,
        string displayName,
        UserPermissions permission = UserPermissions.SystemControl,
        IReadOnlyList<ExtensionParameter>? parameters = null,
        Func<ExtensionExecutionContext, IReadOnlyDictionary<string, string?>, Task<CommandResult>>? execute = null,
        Func<ExtensionExecutionContext, IReadOnlyDictionary<string, string?>, CancellationToken, Task<CommandResult>>? executeWithToken = null)
        : IRemoteCiExtension
    {
        public string Id { get; } = id;
        public string DisplayName { get; } = displayName;
        public UserPermissions RequiredPermission { get; } = permission;
        public string? Icon => null;
        public IReadOnlyList<ExtensionParameter> Parameters { get; } = parameters ?? [];

        public Task<CommandResult> ExecuteAsync(
            ExtensionExecutionContext context,
            IReadOnlyDictionary<string, string?> args,
            CancellationToken cancellationToken) =>
            executeWithToken?.Invoke(context, args, cancellationToken) ??
            execute?.Invoke(context, args) ??
            Task.FromResult(new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "已执行" });
    }
}
