using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ExtensionCommandRouterTests
{
    private readonly RemoteCiExtensionRegistry _registry = new();
    private readonly ExtensionCommandRouter _router;

    public ExtensionCommandRouterTests()
    {
        _router = new ExtensionCommandRouter(_registry, NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task RunExtension_InvokesRegisteredExtensionWithArgsAndIdentity()
    {
        IReadOnlyDictionary<string, string?>? receivedArgs = null;
        UserProfile? receivedUser = null;
        _registry.Register(new ExtensionRegistryTests.FakeExtension(
            "demo.say",
            "喊话",
            permission: UserPermissions.SendNotifications,
            execute: (context, args) =>
            {
                receivedArgs = args;
                receivedUser = context.RequestedBy;
                return Task.FromResult(new CommandResult
                {
                    Success = true,
                    Code = CommandResultCodes.Ok,
                    Message = "已喊话",
                });
            }));

        var user = User(userPermissions: UserPermissions.RunExtensions);
        var result = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "demo.say",
            ExtensionArgs = new Dictionary<string, string?> { ["text"] = "你好" },
            RequestedBy = user,
        });

        Assert.True(result.Success);
        Assert.Equal("已喊话", result.Message);
        Assert.Equal("你好", receivedArgs!["text"]);
        Assert.Equal(user.Id, receivedUser!.Id);
    }

    [Fact]
    public async Task RunExtension_RejectsUnknownExtension()
    {
        var result = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "missing.ext",
            RequestedBy = Admin(),
        });

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.InvalidRequest, result.Code);
    }

    [Fact]
    public async Task RunExtension_RejectsMissingExtensionPermission()
    {
        _registry.Register(new ExtensionRegistryTests.FakeExtension(
            "demo.power",
            "关机",
            permission: UserPermissions.SystemControl));

        var result = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "demo.power",
            RequestedBy = User(userPermissions: UserPermissions.SendNotifications),
        });

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.Forbidden, result.Code);
    }

    [Fact]
    public async Task RunExtension_RejectsExtensionDisabledByServerPolicy()
    {
        _registry.Register(new ExtensionRegistryTests.FakeExtension(
            "demo.policy",
            "策略扩展",
            permission: UserPermissions.SendNotifications));
        var user = User(UserPermissions.RunExtensions);
        user.AllowedExtensionIds = [];

        var result = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "demo.policy",
            RequestedBy = user,
        });

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.Forbidden, result.Code);
    }

    [Fact]
    public async Task RunExtension_RejectsMissingOrBlankExtensionId()
    {
        var missing = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            RequestedBy = Admin(),
        });
        Assert.Equal(CommandResultCodes.InvalidRequest, missing.Code);

        var blank = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = " ",
            RequestedBy = Admin(),
        });
        Assert.Equal(CommandResultCodes.InvalidRequest, blank.Code);
    }

    [Fact]
    public async Task RunExtension_RequiresAuthenticatedRequester()
    {
        _registry.Register(new ExtensionRegistryTests.FakeExtension("demo.a", "扩展 A"));

        var result = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "demo.a",
        });

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.Forbidden, result.Code);
    }

    [Fact]
    public async Task RunExtension_RejectsMissingRequiredParameter()
    {
        _registry.Register(new ExtensionRegistryTests.FakeExtension(
            "demo.required",
            "必填参数",
            parameters: [new ExtensionParameter { Key = "text", Label = "内容", Required = true }]));

        var result = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "demo.required",
            RequestedBy = Admin(),
        });

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.InvalidRequest, result.Code);
        Assert.Contains("内容", result.Message);
    }

    [Fact]
    public async Task RunExtension_ConvertsExtensionExceptionToInternalError()
    {
        _registry.Register(new ExtensionRegistryTests.FakeExtension(
            "demo.broken",
            "会崩的扩展",
            execute: (_, _) => throw new InvalidOperationException("boom")));

        var result = await _router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "demo.broken",
            RequestedBy = Admin(),
        });

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.InternalError, result.Code);
    }

    [Fact]
    public async Task RunExtension_TimesOutHungExtension()
    {
        // 用毫秒级超时验证取消路径；生产环境默认 15 秒。扩展完全不响应取消也能强制放弃。
        var router = new ExtensionCommandRouter(_registry, NullLoggerFactory.Instance, TimeSpan.FromMilliseconds(50));
        _registry.Register(new ExtensionRegistryTests.FakeExtension(
            "demo.hang",
            "挂起的扩展",
            executeWithToken: (_, _, _) => new TaskCompletionSource<CommandResult>().Task));

        var result = await router.RunAsync(new CommandMessage
        {
            Command = CommandKind.RunExtension,
            ExtensionId = "demo.hang",
            RequestedBy = Admin(),
        });

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.Timeout, result.Code);
    }

    [Fact]
    public async Task RunExtension_DuplicateWhileInFlightReturnsBusy()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _registry.Register(new ExtensionRegistryTests.FakeExtension(
            "demo.busy",
            "在途扩展",
            executeWithToken: (_, _, _) =>
            {
                started.TrySetResult();
                return gate.Task;
            }));

        var first = _router.RunAsync(RunCommand("demo.busy"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 上一次执行尚未结束时重复触发必须返回 BUSY，避免同一命令被重复执行。
        var duplicate = await _router.RunAsync(RunCommand("demo.busy"));
        Assert.False(duplicate.Success);
        Assert.Equal(CommandResultCodes.Busy, duplicate.Code);

        gate.SetResult(new CommandResult { Success = true, Code = CommandResultCodes.Ok });
        var completed = await first;
        Assert.True(completed.Success);

        // 执行完成后单飞位释放，新请求可以再次执行。
        var again = await _router.RunAsync(RunCommand("demo.busy"));
        Assert.True(again.Success);
    }

    [Fact]
    public async Task RunExtension_HungExecutionKeepsBusyUntilBackgroundCompletes()
    {
        var router = new ExtensionCommandRouter(_registry, NullLoggerFactory.Instance, TimeSpan.FromMilliseconds(50));
        var gate = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _registry.Register(new ExtensionRegistryTests.FakeExtension(
            "demo.hangbusy",
            "挂死扩展",
            executeWithToken: (_, _, _) => gate.Task));

        var timedOut = await router.RunAsync(RunCommand("demo.hangbusy"));
        Assert.Equal(CommandResultCodes.Timeout, timedOut.Code);

        // 超时只放弃等待，后台任务仍在运行：新请求必须 BUSY 而不是重复触发挂死的扩展。
        var duplicate = await router.RunAsync(RunCommand("demo.hangbusy"));
        Assert.Equal(CommandResultCodes.Busy, duplicate.Code);

        gate.SetResult(new CommandResult { Success = true, Code = CommandResultCodes.Ok });
        // 后台任务结束后单飞位异步释放，短暂轮询等待回收。
        CommandResult retried;
        var attempts = 0;
        do
        {
            await Task.Delay(20);
            retried = await router.RunAsync(RunCommand("demo.hangbusy"));
        } while (retried.Code == CommandResultCodes.Busy && ++attempts < 100);
        Assert.NotEqual(CommandResultCodes.Busy, retried.Code);
    }

    private static CommandMessage RunCommand(string extensionId) => new()
    {
        Command = CommandKind.RunExtension,
        ExtensionId = extensionId,
        RequestedBy = Admin(),
    };

    private static UserProfile Admin() => User(userPermissions: UserPermissions.All);

    private static UserProfile User(UserPermissions userPermissions) => new()
    {
        Id = Guid.NewGuid(),
        Username = "tester",
        DisplayName = "测试用户",
        Permissions = userPermissions,
    };
}
