using Avalonia.Threading;
using RemoteCI.Plugin.Views;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 语音按到达顺序逐条播放：同一时间只有一个浮窗，后到的语音排队，
/// 上一条播完或浮窗被关闭后自动播放下一条，防止不同发送人的声音叠加或打断暂停中的消息。
/// </summary>
public sealed class VoiceMessagePlayer
{
    // 每条最多约 1.9 MB，限制排队数量避免内存被大量语音占满。
    internal const int MaxQueued = 10;

    private readonly Queue<PendingVoice> _queue = new();
    private VoiceMessageWindow? _window;

    public CommandResult Play(byte[] audio, string title, bool forceComplete = false)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!OperatingSystem.IsWindows())
            return CommandResult.Failure(CommandResultCodes.CapabilityUnsupported, "语音播放目前需要 Windows");
        var voice = new PendingVoice(audio, title, forceComplete);
        if (_window is { IsPlaybackFinished: false })
        {
            if (_queue.Count >= MaxQueued)
                return CommandResult.Failure(CommandResultCodes.Busy, "待播放的语音过多，请稍后再发送");
            _queue.Enqueue(voice);
            _window.SetQueuedCount(_queue.Count);
            return new CommandResult
            {
                Success = true, Code = CommandResultCodes.Ok,
                Message = $"语音已排队，将在前面 {_queue.Count} 条播完后自动播放",
            };
        }
        // 上一条已播完只是停留在浮窗上时，直接换成新语音。
        CloseCurrent();
        Show(voice);
        return new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "语音已在 ClassIsland 开始播放" };
    }

    public void Stop() => Dispatcher.UIThread.Post(() =>
    {
        _queue.Clear();
        CloseCurrent();
    });

    private void Show(PendingVoice voice)
    {
        var window = new VoiceMessageWindow(voice.Audio, voice.Title, voice.ForceComplete);
        try
        {
            window.Closed += (_, _) =>
            {
                if (_window != window) return;
                _window = null;
                ShowNext();
            };
            window.PlaybackFinished += (_, _) =>
            {
                if (_window == window && _queue.Count > 0) window.Close();
            };
            _window = window;
            window.SetQueuedCount(_queue.Count);
            window.Show();
            window.StartPlayback();
        }
        catch
        {
            _window = null;
            window.CloseForcibly();
            window.DisposeAudio();
            throw;
        }
    }

    private void ShowNext()
    {
        while (_queue.TryDequeue(out var next))
        {
            // 排队语音没有等待中的回执可以上报，音频设备异常时跳过该条继续播放后续语音。
            try { Show(next); return; }
            catch (Exception) { }
        }
    }

    /// <summary>先解除当前浮窗引用再关闭，关闭事件不会再触发自动播放下一条。</summary>
    private void CloseCurrent()
    {
        var window = _window;
        _window = null;
        window?.CloseForcibly();
    }

    private sealed record PendingVoice(byte[] Audio, string Title, bool ForceComplete);
}
