using Avalonia.Threading;
using ClassIsland.Core;
using FluentAvalonia.UI.Controls;

namespace RemoteCI.Plugin.Services;

/// <summary>用 ClassIsland 主窗口上的任务对话框询问是否允许插件接入；多个插件依次询问，不叠加弹窗。</summary>
internal sealed class ExtensionAccessPrompt : IExtensionAccessPrompt
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<bool?> AskAsync(ExtensionCaller caller)
    {
        await _gate.WaitAsync();
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var dialog = new TaskDialog
                {
                    Title = "RemoteCI",
                    Header = $"是否允许 {caller.Name} 接入 RemoteCI？",
                    Content = $"插件 {caller.Name}（{caller.PluginId}）请求向 RemoteCI 注册远程功能。" +
                        "允许后，这些功能可以被有权限的老师在 WebUI、手机或手表上远程调用。\n" +
                        "拒绝不会影响该插件本身的运行；之后可以在“RemoteCI 设置”中修改。",
                    XamlRoot = AppBase.Current.GetRootWindow(),
                    Buttons =
                    {
                        new TaskDialogButton("允许", true) { IsDefault = true },
                        new TaskDialogButton("拒绝", false),
                    },
                };
                var result = await dialog.ShowAsync();
                return result is bool allowed ? allowed : (bool?)null;
            });
        }
        catch (Exception ex)
        {
            // 没能弹窗（如主窗口尚未就绪）：本次保持待定，下次启动再问。
            Console.Error.WriteLine($"RemoteCI 扩展接入询问失败：{ex.Message}");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
