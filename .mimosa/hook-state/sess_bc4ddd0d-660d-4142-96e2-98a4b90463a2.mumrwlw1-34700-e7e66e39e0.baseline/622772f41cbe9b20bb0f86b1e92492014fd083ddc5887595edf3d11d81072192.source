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
