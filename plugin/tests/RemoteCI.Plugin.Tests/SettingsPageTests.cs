using Avalonia.Controls;
using RemoteCI.Plugin.Services;
using RemoteCI.Plugin.Settings;
using RemoteCI.Plugin.Views.SettingsPages;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class SettingsPageTests
{
    [Fact]
    public void StandardSettings_DoesNotExposeConnectionDisableToggles()
    {
        var page = new RemoteCiSettingsPage(new PluginSettings());

        Assert.DoesNotContain("启用云端中转", CheckBoxLabels(page.Content));
        Assert.DoesNotContain("启用局域网直连服务", CheckBoxLabels(page.Content));
    }

    [Fact]
    public void StandardSettings_ExposesManualSchedulePush()
    {
        var page = new RemoteCiSettingsPage(new PluginSettings());

        Assert.Contains("立即推送当前课表", ButtonLabels(page.Content));
    }

    [Fact]
    public void StandardSettings_ExposesServerConnectionTest()
    {
        var page = new RemoteCiSettingsPage(new PluginSettings());

        Assert.Contains("测试服务器连接", ButtonLabels(page.Content));
    }

    [Fact]
    public void DeveloperSettingsMenu_IsHiddenByDefaultAndCanBeEnabled()
    {
        var settings = new PluginSettings();
        var page = new RemoteCiSettingsPage(settings);

        Assert.False(settings.ShowDeveloperSettingsMenu);
        Assert.False(Plugin.ShouldRegisterDeveloperSettingsPage(settings));
        Assert.Contains("显示开发者设置菜单", TextLabels(page.Content));

        settings.ShowDeveloperSettingsMenu = true;
        Assert.True(Plugin.ShouldRegisterDeveloperSettingsPage(settings));
    }

    [Fact]
    public void ConnectionStatusText_ShowsCurrentServerState()
    {
        var status = new CloudConnectionStatus(
            CloudConnectionState.WaitingToRetry,
            "连接失败，将在 5 秒后自动重试",
            "WebSocket 连接失败",
            DateTimeOffset.UtcNow);

        Assert.Equal(
            "服务器状态：连接失败，将在 5 秒后自动重试",
            RemoteCiSettingsPage.ConnectionStatusText(status));
    }

    [Fact]
    public void ScheduleStatusText_LeavesRunningStateAfterCompletion()
    {
        var running = RemoteCiSettingsPage.ScheduleStatusText(new ScheduleSyncStatus
        {
            State = ScheduleSyncTaskState.Running,
            Message = "正在执行插件端推送课表任务",
        });
        var completed = RemoteCiSettingsPage.ScheduleStatusText(new ScheduleSyncStatus
        {
            State = ScheduleSyncTaskState.Completed,
            Message = "课表已生成并推送完成",
        });

        Assert.Contains("请勿重复操作", running);
        Assert.Equal("课表已生成并推送完成", completed);
        Assert.DoesNotContain("正在", completed);
    }

    [Fact]
    public void DeveloperSettings_ExposesConnectionDisableToggles()
    {
        var page = new RemoteCiDeveloperSettingsPage(new PluginSettings { EnableCloud = false });

        Assert.Contains("启用云端中转", CheckBoxLabels(page.Content));
        Assert.Contains("启用局域网直连服务", CheckBoxLabels(page.Content));
    }

    [Fact]
    public void DeveloperSettings_ExposesRealVoiceReceiveTest()
    {
        var page = new RemoteCiDeveloperSettingsPage(new PluginSettings());
        var command = RemoteCiDeveloperSettingsPage.CreateTestVoiceCommand();

        Assert.Contains("测试接收语音", ButtonLabels(page.Content));
        Assert.Equal(CommandKind.SendVoiceMessage, command.Command);
        Assert.True(command.RequestedBy!.Permissions.HasFlag(UserPermissions.SendVoiceMessages));
        Assert.True(VoiceMessageRequest.TryDecode(command.VoiceMessage, out var audio));
        Assert.Equal(VoiceMessageRequest.SampleRate * sizeof(short) * 2, audio.Length);
    }

    [Theory]
    [InlineData("8765", "http://nas:8080", true, true)]
    [InlineData("0", "http://nas:8080", false, true)]
    [InlineData("70000", "http://nas:8080", false, true)]
    [InlineData("abc", "http://nas:8080", false, true)]
    [InlineData("8765", "nas:8080", true, false)]
    [InlineData("8765", "javascript:alert(1)", true, false)]
    [InlineData("8765", "", true, true)]
    [InlineData("8765", "https://ci.example.com", true, true)]
    public void ValidateInput_GuardsPortRangeAndUrlScheme(
        string portText, string urlText, bool expectedPortValid, bool expectedUrlValid)
    {
        var (portValid, urlValid) = RemoteCiSettingsPage.ValidateInput(portText, urlText);

        Assert.Equal(expectedPortValid, portValid);
        Assert.Equal(expectedUrlValid, urlValid);
    }

    private static IReadOnlyList<string> ButtonLabels(object? root)
    {
        var labels = new List<string>();
        Visit(root);
        return labels;

        void Visit(object? node)
        {
            if (node is Button { Content: not null } button)
                labels.Add(button.Content.ToString()!);
            if (node is ContentControl contentControl) Visit(contentControl.Content);
            if (node is Panel panel)
            {
                foreach (var child in panel.Children) Visit(child);
            }
        }
    }

    private static IReadOnlyList<string> CheckBoxLabels(object? root)
    {
        var labels = new List<string>();
        Visit(root);
        return labels;

        void Visit(object? node)
        {
            if (node is CheckBox { Content: not null } checkBox)
                labels.Add(checkBox.Content.ToString()!);
            if (node is ContentControl contentControl) Visit(contentControl.Content);
            if (node is Panel panel)
            {
                foreach (var child in panel.Children) Visit(child);
            }
        }
    }

    private static IReadOnlyList<string> TextLabels(object? root)
    {
        var labels = new List<string>();
        Visit(root);
        return labels;

        void Visit(object? node)
        {
            if (node is TextBlock { Text: not null } textBlock) labels.Add(textBlock.Text);
            if (node is ContentControl contentControl) Visit(contentControl.Content);
            if (node is Panel panel)
            {
                foreach (var child in panel.Children) Visit(child);
            }
        }
    }
}
