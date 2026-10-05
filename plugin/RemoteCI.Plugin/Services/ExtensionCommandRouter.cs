using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Extensions;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 扩展命令的执行路由：RunExtension 先校验独立扩展权限和服务端开放策略，再按声明校验参数并调用注册方回调；
/// ApplyExtensionSettings 校验分组与设置字段后调用分组的设置回调。异常与超时统一转换为 CommandResult。
/// </summary>
internal sealed class ExtensionCommandRouter
{
    private readonly IRemoteCiExtensionRegistry _registry;
    private readonly ILogger<ExtensionCommandRouter> _logger;
    private readonly TimeSpan _timeout;
    // 每个扩展 Id 同一时刻只允许一次在途执行：重复触发返回 BUSY，
    // 避免同一命令被重复执行（如重复广播/重复外部调用）。
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    // 设置写入按分组单飞，与扩展执行分开计数，避免分组 Id 与扩展 Id 相同时互相阻塞。
    private readonly ConcurrentDictionary<string, byte> _settingsInFlight = new(StringComparer.Ordinal);

    public ExtensionCommandRouter(
        IRemoteCiExtensionRegistry registry,
        ILoggerFactory loggerFactory,
        TimeSpan? timeout = null)
    {
        _registry = registry;
        _logger = loggerFactory.CreateLogger<ExtensionCommandRouter>();
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task<CommandResult> RunAsync(CommandMessage command)
    {
        if (command.RequestedBy is null)
            return CommandResult.Failure(CommandResultCodes.Forbidden, "权限不足");

        var id = command.ExtensionId;
        if (string.IsNullOrWhiteSpace(id))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "缺少扩展 Id");

        var extension = _registry.GetExtensions()
            .FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (extension is null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, $"扩展功能不存在：{id}");
        if (!ExtensionAccess.CanInvoke(command.RequestedBy, new ExtensionDefinition
            {
                Id = extension.Id,
            }))
            return CommandResult.Failure(CommandResultCodes.Forbidden, "权限不足或扩展未开放");

        var args = ExtensionFieldValidator.ValidateArguments(extension.Parameters, command.ExtensionArgs, out var error);
        if (error is not null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, error);

        var context = new ExtensionExecutionContext { RequestedBy = command.RequestedBy };
        return await RunGuardedAsync(
            _inFlight,
            id,
            "该扩展上一次执行尚未结束，请稍后重试",
            token => extension.ExecuteAsync(context, args, token),
            "RemoteCI 扩展执行失败：{Target}",
            "扩展执行异常，请查看 ClassIsland 日志",
            "扩展执行");
    }

    public async Task<CommandResult> ApplySettingsAsync(CommandMessage command)
    {
        if (command.RequestedBy is null || !command.RequestedBy.Permissions.HasFlag(UserPermissions.RunExtensions))
            return CommandResult.Failure(CommandResultCodes.Forbidden, "权限不足");
        if (command.ExtensionSettings is not { } request || string.IsNullOrWhiteSpace(request.GroupId))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "缺少扩展分组 Id");

        var group = _registry.GetGroups()
            .FirstOrDefault(x => string.Equals(x.Id, request.GroupId, StringComparison.Ordinal));
        if (group is null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, $"扩展分组不存在：{request.GroupId}");
        var fields = group.Settings ?? [];
        if (fields.Count == 0)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "该扩展分组没有可修改的设置");

        var values = ExtensionFieldValidator.ValidateSettings(fields, request.Values, out var error);
        if (error is not null)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, error);

        var context = new ExtensionExecutionContext { RequestedBy = command.RequestedBy };
        var result = await RunGuardedAsync(
            _settingsInFlight,
            group.Id,
            "该扩展分组上一次设置尚未完成，请稍后重试",
            token => group.ApplySettingsAsync(context, values, token),
            "RemoteCI 扩展设置写入失败：{Target}",
            "扩展设置写入异常，请查看 ClassIsland 日志",
            "扩展设置写入");
        // 无论成功与否都重新上报当前值：失败时插件可能已经部分写入，服务端预填需要反映真实状态。
        _registry.NotifySettingsChanged(group.Id);
        return result;
    }

    /// <summary>
    /// 与协议回执上限对齐的统一执行壳：同一目标单飞、超时强制放弃等待、异常转为 INTERNAL_ERROR。
    /// 取消令牌只是礼貌请求，超时必须强制放弃等待，不能依赖第三方插件自行响应。
    /// </summary>
    private async Task<CommandResult> RunGuardedAsync(
        ConcurrentDictionary<string, byte> inFlight,
        string key,
        string busyMessage,
        Func<CancellationToken, Task<CommandResult>> action,
        string logTemplate,
        string failureMessage,
        string actionName)
    {
        if (!inFlight.TryAdd(key, 0))
            return CommandResult.Failure(CommandResultCodes.Busy, busyMessage);

        try
        {
            using var timeout = new CancellationTokenSource(_timeout);
            var execution = action(timeout.Token);
            var completed = await Task.WhenAny(execution, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
            if (completed != execution)
            {
                // 回调挂死：强制放弃等待后不能立即释放单飞标记，否则下一次触发会在
                // 上一次仍在后台运行时重复执行；等后台任务真正结束后再释放。
                _ = execution.ContinueWith(
                    finished => inFlight.TryRemove(key, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return CommandResult.Failure(CommandResultCodes.Timeout, $"{actionName}超过 {_timeout.TotalSeconds:0} 秒已取消");
            }
            inFlight.TryRemove(key, out _);
            return await execution ?? CommandResult.Failure(CommandResultCodes.InternalError, $"{actionName}未返回结果");
        }
        catch (OperationCanceledException)
        {
            inFlight.TryRemove(key, out _);
            return CommandResult.Failure(CommandResultCodes.Timeout, $"{actionName}超过 {_timeout.TotalSeconds:0} 秒已取消");
        }
        catch (Exception ex)
        {
            inFlight.TryRemove(key, out _);
            _logger.LogError(ex, logTemplate, key);
            return CommandResult.Failure(CommandResultCodes.InternalError, failureMessage);
        }
    }
}
