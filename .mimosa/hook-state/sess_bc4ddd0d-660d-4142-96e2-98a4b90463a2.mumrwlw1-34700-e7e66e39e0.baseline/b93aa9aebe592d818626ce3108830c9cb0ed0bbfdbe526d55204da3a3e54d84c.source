using Avalonia.Threading;
using RemoteCI.Plugin.Views;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>同一时间仅保留一条语音，防止不同发送人的声音叠加或打断暂停中的消息。</summary>
public sealed class VoiceMessagePlayer
{
    private VoiceMessageWindow? _window;

    public CommandResult Play(byte[] audio, string title)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!OperatingSystem.IsWindows())
            return CommandResult.Failure(CommandResultCodes.CapabilityUnsupported, "语音播放目前需要 Windows");
        if (_window is not null)
            return CommandResult.Failure(CommandResultCodes.Busy, "已有语音浮窗，请关闭后再发送");
        var window = new VoiceMessageWindow(audio, title);
        try
        {
            window.Closed += (_, _) => { if (_window == window) _window = null; };
            _window = window;
            window.Show();
            window.StartPlayback();
            return new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "语音已在 ClassIsland 开始播放" };
        }
        catch
        {
            _window = null;
            window.Close();
            window.DisposeAudio();
            throw;
        }
    }

    public void Stop() => Dispatcher.UIThread.Post(() => _window?.Close());
}
