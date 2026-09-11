using System.Buffers.Binary;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using RemoteCI.Plugin.Settings;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Views.SettingsPages;

/// <summary>集中放置可能破坏远程可用性的高级连接开关，避免普通设置页误操作。</summary>
[SettingsPageInfo("remoteci.plugin.developer", "RemoteCI 开发者设置")]
public sealed class RemoteCiDeveloperSettingsPage : SettingsPageBase
{
    private readonly PluginSettings _settings;
    private readonly CheckBox _cloudCheck;
    private readonly CheckBox _lanCheck;
    private readonly TextBlock _hint;
    private readonly Button _testVoiceButton;
    private readonly CommandHandler? _commandHandler;

    public RemoteCiDeveloperSettingsPage(PluginSettings settings, CommandHandler? commandHandler = null)
    {
        _settings = settings;
        _commandHandler = commandHandler;
        _cloudCheck = new CheckBox
        {
            Content = "启用云端中转",
            IsChecked = settings.EnableCloud,
        };
        _lanCheck = new CheckBox
        {
            Content = "启用局域网直连服务",
            IsChecked = settings.EnableLanServer,
        };
        _hint = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var saveButton = new Button { Content = "保存开发者设置" };
        saveButton.Click += OnSaveClick;
        _testVoiceButton = new Button
        {
            Content = "测试接收语音",
            IsEnabled = commandHandler is not null,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _testVoiceButton.Click += OnTestVoiceClick;

        Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel
            {
                Spacing = 10,
                Margin = new Avalonia.Thickness(16),
                Children =
                {
                    new TextBlock
                    {
                        Text = "RemoteCI 开发者设置",
                        FontSize = 20,
                        FontWeight = Avalonia.Media.FontWeight.Bold,
                    },
                    new TextBlock
                    {
                        Text = "这些开关会改变 RemoteCI 的连接能力。关闭云端后无法向 WebUI 同步，关闭局域网后手表无法直连插件；仅在诊断网络时使用。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    _cloudCheck,
                    _lanCheck,
                    saveButton,
                    new TextBlock
                    {
                        Text = "测试",
                        FontSize = 16,
                        FontWeight = Avalonia.Media.FontWeight.SemiBold,
                        Margin = new Avalonia.Thickness(0, 8, 0, 0),
                    },
                    new TextBlock
                    {
                        Text = "按真实接收流程显示强调通知、自动播放测试音频并打开语音浮窗。",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    _testVoiceButton,
                    _hint,
                },
            },
        };
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        _settings.EnableCloud = _cloudCheck.IsChecked == true;
        _settings.EnableLanServer = _lanCheck.IsChecked == true;
        // 属性变更已由 Plugin.cs 的 PropertyChanged 订阅自动落盘，无需重复写 Settings.json。
        _hint.Text = "已保存。重启 ClassIsland 后生效。";
    }

    private async void OnTestVoiceClick(object? sender, RoutedEventArgs e)
    {
        if (_commandHandler is null) return;
        _testVoiceButton.IsEnabled = false;
        _hint.Text = "正在按完整流程接收测试语音……";
        try
        {
            var result = await _commandHandler.HandleAsync(CreateTestVoiceCommand());
            _hint.Text = result.Message;
        }
        finally
        {
            _testVoiceButton.IsEnabled = true;
        }
    }

    internal static CommandMessage CreateTestVoiceCommand()
    {
        const int durationSeconds = 2;
        var audio = new byte[VoiceMessageRequest.SampleRate * sizeof(short) * durationSeconds];
        for (var sampleIndex = 0; sampleIndex < audio.Length / sizeof(short); sampleIndex++)
        {
            var seconds = sampleIndex / (double)VoiceMessageRequest.SampleRate;
            var fade = Math.Min(1, Math.Min(seconds / 0.03, (durationSeconds - seconds) / 0.08));
            var signal = 0.5 * Math.Sin(2 * Math.PI * 523.25 * seconds) +
                         0.3 * Math.Sin(2 * Math.PI * 659.25 * seconds) +
                         0.2 * Math.Sin(2 * Math.PI * 783.99 * seconds);
            var value = (short)(short.MaxValue * 0.18 * fade * signal);
            BinaryPrimitives.WriteInt16LittleEndian(audio.AsSpan(sampleIndex * sizeof(short)), value);
        }

        return new CommandMessage
        {
            Command = CommandKind.SendVoiceMessage,
            RequestedBy = new UserProfile
            {
                DisplayName = "RemoteCI 测试",
                Permissions = UserPermissions.SendVoiceMessages,
            },
            VoiceMessage = new VoiceMessageRequest { AudioBase64 = Convert.ToBase64String(audio) },
        };
    }
}
