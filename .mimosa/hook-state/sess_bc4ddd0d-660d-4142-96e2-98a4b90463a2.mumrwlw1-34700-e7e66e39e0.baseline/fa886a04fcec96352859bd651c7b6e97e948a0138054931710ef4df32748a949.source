using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Extensions;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// RunExtension 命令的执行路由：先校验独立扩展权限和服务端开放策略，再校验扩展声明的最小权限，
/// 再调用注册方回调，并把异常统一转换为 CommandResult。
/// </summary>
internal sealed class ExtensionCommandRouter
{
    private readonly IRemoteCiExtensionRegistry _registry;
    private readonly ILogger<ExtensionCommandRouter> _logger;
    private readonly TimeSpan _timeout;
    // 每个扩展 Id 同一时刻只允许一次在途执行：重复触发返回 BUSY，
    // 避免同一命令被重复执行（如重复广播/重复外部调用）。
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);

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

        var args = command.ExtensionArgs ?? new Dictionary<string, string?>();
        var missing = extension.Parameters
            .Where(p => p.Required && string.IsNullOrWhiteSpace(args.GetValueOrDefault(p.Key)))
            .Select(p => p.Label)
            .ToList();
        if (missing.Count > 0)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, $"缺少参数：{string.Join("、", missing)}");
        // 参数值统一限长，避免超大载荷击穿消息缓冲或塞满扩展日志。
        if (args.Any(pair => pair.Value is { Length: > 4096 }))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "扩展参数过长（单个参数不能超过 4096 个字符）");

        if (!_inFlight.TryAdd(id, 0))
            return CommandResult.Failure(CommandResultCodes.Busy, "该扩展上一次执行尚未结束，请稍后重试");

        try
        {
            // 与协议回执上限对齐：单个挂死的扩展不能阻塞云端/局域网消息处理。
            // 取消令牌只是礼貌请求，超时必须强制放弃等待，不能依赖扩展自行响应。
            using var timeout = new CancellationTokenSource(_timeout);
            var execution = extension.ExecuteAsync(
                new ExtensionExecutionContext { RequestedBy = command.RequestedBy },
                args,
                timeout.Token);
            var completed = await Task.WhenAny(execution, Task.Delay(Timeout.InfiniteTimeSpan, timeout.Token));
            if (completed != execution)
            {
                // 扩展挂死：强制放弃等待后不能立即释放单飞标记，否则下一次触发会在
                // 上一次仍在后台运行时重复执行；等后台任务真正结束后再释放。
                _ = execution.ContinueWith(
                    finished => _inFlight.TryRemove(id, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return CommandResult.Failure(CommandResultCodes.Timeout, $"扩展执行超过 {_timeout.TotalSeconds:0} 秒已取消");
            }
            _inFlight.TryRemove(id, out _);
            return await execution ?? CommandResult.Failure(CommandResultCodes.InternalError, "扩展未返回执行结果");
        }
        catch (OperationCanceledException)
        {
            _inFlight.TryRemove(id, out _);
            return CommandResult.Failure(CommandResultCodes.Timeout, $"扩展执行超过 {_timeout.TotalSeconds:0} 秒已取消");
        }
        catch (Exception ex)
        {
            _inFlight.TryRemove(id, out _);
            _logger.LogError(ex, "RemoteCI 扩展执行失败：{ExtensionId}", id);
            return CommandResult.Failure(CommandResultCodes.InternalError, "扩展执行异常，请查看 ClassIsland 日志");
        }
    }
}
