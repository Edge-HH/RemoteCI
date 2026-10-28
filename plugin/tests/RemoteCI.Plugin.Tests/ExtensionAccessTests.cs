using RemoteCI.Plugin.Services;
using RemoteCI.Plugin.Views.SettingsPages;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ExtensionAccessTests : IDisposable
{
    private static readonly ExtensionCaller Demo = new("demo.plugin", "演示插件");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "remoteci-access-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_dir, "ExtensionAccess.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task UnknownPlugin_IsHiddenUntilAllowed_AndDecisionPersists()
    {
        var prompt = new FakePrompt();
        var access = new ExtensionAccessService(StorePath, prompt);
        var registry = new RemoteCiExtensionRegistry(access, () => Demo);
        var changed = 0;
        registry.ExtensionsChanged += (_, _) => changed++;

        registry.Register(new ExtensionRegistryTests.FakeExtension("demo.a", "扩展 A"));
        registry.RegisterGroup(new ExtensionRegistryTests.FakeGroup("demo.group", "演示分组"));

        Assert.Empty(registry.GetExtensions());
        Assert.Empty(registry.GetGroups());
        Assert.Equal(Demo, Assert.Single(registry.GetCallers()));
        Assert.Equal(1, prompt.Asked);

        var decided = NextChange(access);
        prompt.Answer(true);
        await decided;
        Assert.Equal("demo.a", Assert.Single(registry.GetExtensions()).Id);
        Assert.Single(registry.GetGroups());
        Assert.True(changed >= 2);

        // 重新启动后直接按已保存的决定处理，不再询问。
        var restartedPrompt = new FakePrompt();
        var restarted = new RemoteCiExtensionRegistry(new ExtensionAccessService(StorePath, restartedPrompt), () => Demo);
        restarted.Register(new ExtensionRegistryTests.FakeExtension("demo.a", "扩展 A"));
        Assert.Single(restarted.GetExtensions());
        Assert.Equal(0, restartedPrompt.Asked);
    }

    [Fact]
    public async Task DeniedPlugin_RegistrationSucceedsButStaysHidden()
    {
        var prompt = new FakePrompt();
        var access = new ExtensionAccessService(StorePath, prompt);
        var registry = new RemoteCiExtensionRegistry(access, () => Demo);

        registry.Register(new ExtensionRegistryTests.FakeExtension("demo.a", "扩展 A"));
        var decided = NextChange(access);
        prompt.Answer(false);
        await decided;

        Assert.Empty(registry.GetExtensions());
        Assert.Equal(ExtensionAccessState.Denied, Assert.Single(access.Snapshot()).State);
        // 拒绝后再注册也不会重复询问，也不抛异常。
        registry.Register(new ExtensionRegistryTests.FakeExtension("demo.b", "扩展 B"));
        Assert.Equal(1, prompt.Asked);
    }

    [Fact]
    public void PreGrantedPlugin_IsVisibleWithoutPrompt()
    {
        var prompt = new FakePrompt();
        var access = new ExtensionAccessService(StorePath, prompt);
        access.Grant(["demo.plugin"]);
        var registry = new RemoteCiExtensionRegistry(access, () => Demo);

        registry.Register(new ExtensionRegistryTests.FakeExtension("demo.a", "扩展 A"));

        Assert.Single(registry.GetExtensions());
        Assert.Equal(0, prompt.Asked);
        // 首次接入时用插件真实名称替换预授权时的 Id 占位。
        Assert.Equal("演示插件", Assert.Single(access.Snapshot()).Name);
    }

    [Fact]
    public void LockedPlugin_CannotBeRevokedLocally_UntilUnlocked()
    {
        var access = new ExtensionAccessService(StorePath, null);
        var locked = access.Apply(new ExtensionAccessRequest { Action = ExtensionAccessAction.Lock, PluginIds = ["demo.plugin", " "] });

        Assert.True(locked.Success);
        Assert.True(access.IsAllowed("demo.plugin"));
        Assert.False(access.TrySetLocal(Demo, false, out var error));
        Assert.NotNull(error);
        Assert.True(access.IsAllowed("demo.plugin"));

        Assert.True(access.Apply(new ExtensionAccessRequest { Action = ExtensionAccessAction.Unlock, PluginIds = ["demo.plugin"] }).Success);
        Assert.True(access.IsAllowed("demo.plugin"));
        Assert.True(access.TrySetLocal(Demo, false, out _));
        Assert.False(access.IsAllowed("demo.plugin"));
    }

    [Fact]
    public void Apply_RejectsEmptyOrRemoteCiOnlyRequests()
    {
        var access = new ExtensionAccessService(StorePath, null);

        Assert.Equal(CommandResultCodes.InvalidRequest, access.Apply(null).Code);
        Assert.Equal(CommandResultCodes.InvalidRequest, access.Apply(new ExtensionAccessRequest
        {
            Action = ExtensionAccessAction.Lock,
            PluginIds = [PluginManagementService.RemoteCiPluginId],
        }).Code);
    }

    [Fact]
    public void UndecidedCaller_CanBeDecidedFromSettingsPage()
    {
        var access = new ExtensionAccessService(StorePath, null);
        var registry = new RemoteCiExtensionRegistry(access, () => Demo);
        registry.Register(new ExtensionRegistryTests.FakeExtension("demo.a", "扩展 A"));

        var rows = RemoteCiSettingsPage.BuildExtensionAccessRows(access.Snapshot(), registry.GetCallers());
        Assert.Equal("等待确认", RemoteCiSettingsPage.ExtensionAccessStateText(Assert.Single(rows)));

        Assert.True(access.TrySetLocal(Demo, true, out _));
        Assert.Single(registry.GetExtensions());
        rows = RemoteCiSettingsPage.BuildExtensionAccessRows(access.Snapshot(), registry.GetCallers());
        Assert.Equal("已授权", RemoteCiSettingsPage.ExtensionAccessStateText(Assert.Single(rows)));
    }

    [Fact]
    public void PromptDismissed_StaysUndecided()
    {
        var prompt = new FakePrompt();
        prompt.Answer(null);
        var access = new ExtensionAccessService(StorePath, prompt);
        Assert.False(access.RequestAccess(Demo));

        Assert.Empty(access.Snapshot());
        Assert.False(access.IsAllowed(Demo.PluginId));
    }

    [Fact]
    public void IsUnder_MatchesOnlyFilesInsidePluginFolder()
    {
        var folder = Path.Combine(_dir, "Plugins", "demo");
        Assert.True(ExtensionCallerResolver.IsUnder(Path.Combine(folder, "demo.dll"), folder));
        Assert.False(ExtensionCallerResolver.IsUnder(Path.Combine(_dir, "Plugins", "demo2", "demo.dll"), folder));
        Assert.False(ExtensionCallerResolver.IsUnder(Path.Combine(folder, "demo.dll"), null));
    }

    [Fact]
    public void IsEntranceAssembly_ComparesFileNameWithoutExtension()
    {
        Assert.True(ExtensionCallerResolver.IsEntranceAssembly("Demo.Plugin", "demo.plugin.dll"));
        Assert.False(ExtensionCallerResolver.IsEntranceAssembly("Demo.Plugin", "Demo.Plugin.Extra.dll"));
        Assert.False(ExtensionCallerResolver.IsEntranceAssembly("Demo.Plugin", null));
    }

    private static Task NextChange(ExtensionAccessService access)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        access.Changed += () => changed.TrySetResult();
        return changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FakePrompt : IExtensionAccessPrompt
    {
        private readonly TaskCompletionSource<bool?> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Asked { get; private set; }

        public Task<bool?> AskAsync(ExtensionCaller caller)
        {
            Asked++;
            return _answer.Task;
        }

        public void Answer(bool? allowed) => _answer.TrySetResult(allowed);
    }
}
