using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>ClassIsland 主界面文件的远程上传与获取；所有宿主访问都在 UI 线程执行。</summary>
public sealed class MainWindowLayoutService(ILogger<MainWindowLayoutService> logger)
{
    public async Task<CommandResult> ApplyAsync(MainWindowLayoutApplyRequest? request) =>
        await Dispatcher.UIThread.InvokeAsync(() => MainWindowLayoutExecutor.Apply(request, CreateBackend(),
            error => logger.LogError(error, "上传 ClassIsland 主界面文件失败")));

    public async Task<CommandResult> ReadAsync() =>
        await Dispatcher.UIThread.InvokeAsync(() => MainWindowLayoutExecutor.Read(CreateBackend(),
            error => logger.LogError(error, "读取 ClassIsland 主界面文件失败")));

    private static ClassIslandMainWindowLayoutBackend CreateBackend() =>
        new(IAppHost.GetService<IComponentsService>());
}
