using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using ClassIsland.Core.Abstractions.Controls;
using ClassIsland.Core.Attributes;
using RemoteCI.Plugin.Services;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Views.SettingsPages;

/// <summary>
/// RemoteCI 插件设置页（代码构建 UI，避免引入 Avalonia XAML 编译依赖）。
/// 修改后需重启 ClassIsland 生效。
/// </summary>
[SettingsPageInfo("remoteci.plugin.settings", "RemoteCI 设置")]
public sealed class RemoteCiSettingsPage : SettingsPageBase
{
    private readonly PluginSettings _settings;
    private readonly RemoteCiService? _service;
    private readonly TextBox _portBox;
    private readonly TextBox _cloudUrlBox;
    private readonly TextBox _pairCodeBox;
    private readonly TextBlock _httpWarning;
    private readonly Button _pushScheduleButton;
    private readonly Button _testConnectionButton;
    private readonly TextBlock _connectionStatus;
    private readonly TextBlock _connectionError;
    private readonly TextBlock _connectionTestHint;
    private readonly TextBlock _hint;
    private readonly ToggleSwitch _showDeveloperSettingsToggle;

    public RemoteCiSettingsPage(PluginSettings settings, RemoteCiService? service = null)
    {
        _settings = settings;
        _service = service;

        _portBox = new TextBox { Text = settings.LanServerPort.ToString(), Watermark = "端口（默认 8765）" };
        _cloudUrlBox = new TextBox { Text = settings.CloudServerUrl, Watermark = "云端地址，如 https://nas:8080" };
        _pairCodeBox = new TextBox { Text = settings.PluginPairCode, Watermark = "WebUI 生成的一次性插件配对码" };
        _showDeveloperSettingsToggle = new ToggleSwitch
        {
            IsChecked = settings.ShowDeveloperSettingsMenu,
            OnContent = "开",
            OffContent = "关",
        };

        // 明文 HTTP 会把配对码、密码与课表数据暴露给同网段任何设备，必须醒目提示。
        _httpWarning = new TextBlock
        {
            Text = "⚠ 当前为明文 HTTP 地址：配对码、密码与课表数据在网络中明文传输，可被同网段设备窃听。生产环境请改用 HTTPS。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brushes.OrangeRed,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            IsVisible = IsHttpUrl(settings.CloudServerUrl),
        };
        _cloudUrlBox.TextChanged += (_, _) => _httpWarning.IsVisible = IsHttpUrl(_cloudUrlBox.Text);

        var saveButton = new Button { Content = "保存设置" };
        saveButton.Click += OnSaveClick;
        _pushScheduleButton = new Button { Content = "立即推送当前课表" };
        _pushScheduleButton.Click += OnPushScheduleClick;
        _testConnectionButton = new Button
        {
            Content = "测试服务器连接",
            IsEnabled = service is not null,
        };
        _testConnectionButton.Click += OnTestConnectionClick;
        _connectionStatus = new TextBlock
        {
            Text = "服务器状态：RemoteCI 服务尚未启动",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
        };
        _connectionError = new TextBlock
        {
            Text = string.Empty,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Foreground = Avalonia.Media.Brushes.OrangeRed,
            IsVisible = false,
        };
        _connectionTestHint = new TextBlock
        {
            Text = service is null ? "打开设置页时未取得 RemoteCI 服务，需重启 ClassIsland 后再测试。" : string.Empty,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        _hint = new TextBlock { Text = string.Empty, TextWrapping = Avalonia.Media.TextWrapping.Wrap };

        var panel = new StackPanel
        {
            Spacing = 10,
            Margin = new Avalonia.Thickness(16),
            Children =
            {
                new TextBlock { Text = "RemoteCI 课表手表联动", FontSize = 20, FontWeight = Avalonia.Media.FontWeight.Bold },
                new TextBlock { Text = "把当前状态和七日课表推送到 Wear OS，支持按权限换课与发送通知。", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = "服务器连接", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold },
                        _connectionStatus,
                        _connectionError,
                        _testConnectionButton,
                        _connectionTestHint,
                    },
                },
                new StackPanel
                {
                    Spacing = 6,
                    Children = { new TextBlock { Text = "局域网端口" }, _portBox },
                },
                new StackPanel
                {
                    Spacing = 6,
                    Children = { new TextBlock { Text = "云端服务端地址" }, _cloudUrlBox, _httpWarning },
                },
                new StackPanel
                {
                    Spacing = 6,
                    Children = { new TextBlock { Text = "一次性插件配对码" }, _pairCodeBox },
                },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    ColumnSpacing = 12,
                    Children =
                    {
                        new StackPanel
                        {
                            Spacing = 2,
                            Children =
                            {
                                new TextBlock { Text = "显示开发者设置菜单" },
                                new TextBlock
                                {
                                    Text = "开发者功能用于诊断和测试，修改后需重启 ClassIsland。",
                                    Opacity = 0.7,
                                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                                },
                            },
                        },
                        new Border { Child = _showDeveloperSettingsToggle, [Grid.ColumnProperty] = 1 },
                    },
                },
                saveButton,
                _pushScheduleButton,
                _hint,
            },
        };

        Content = new ScrollViewer
        {
            Content = panel,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        if (_service is not null)
        {
            _service.ScheduleSyncStatusChanged += OnScheduleSyncStatusChanged;
            _service.CloudConnectionStatusChanged += OnCloudConnectionStatusChanged;
            if (_service.CurrentScheduleSyncStatus is { } current) ApplyScheduleSyncStatus(current);
            ApplyCloudConnectionStatus(_service.CurrentCloudConnectionStatus);
        }
    }

    /// <summary>端口必须在 1-65535；云端地址留空或为合法 http/https URI 才有效。</summary>
    internal static (bool PortValid, bool UrlValid) ValidateInput(string? portText, string? urlText)
    {
        var portValid = int.TryParse(portText, out var port) && port is >= 1 and <= 65535;
        var url = urlText?.Trim() ?? string.Empty;
        // 必须给出可解析的 http/https 地址，否则 UriBuilder 会生成畸形目标导致连接永远失败。
        var urlValid = string.IsNullOrWhiteSpace(url) ||
            Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) &&
            parsedUrl.Scheme is "http" or "https";
        return (portValid, urlValid);
    }

    /// <summary>地址是否为明文 HTTP；非法或留空地址不触发警告（由保存校验拦截）。</summary>
    internal static bool IsHttpUrl(string? urlText)
    {
        var url = urlText?.Trim();
        if (string.IsNullOrWhiteSpace(url)) return false;
        return Uri.TryCreate(url, UriKind.Absolute, out var parsed) && parsed.Scheme == "http";
    }

    private void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        var (portValid, urlValid) = ValidateInput(_portBox.Text, _cloudUrlBox.Text);
        var urlText = _cloudUrlBox.Text?.Trim() ?? string.Empty;
        if (!portValid || !urlValid)
        {
            _hint.Text = !portValid
                ? "端口必须是 1-65535 的数字，未保存。"
                : "云端地址必须是以 http:// 或 https:// 开头的完整地址，未保存。";
            return;
        }

        _settings.LanServerPort = int.Parse(_portBox.Text!.Trim());
        _settings.CloudServerUrl = string.IsNullOrWhiteSpace(urlText)
            ? "http://localhost:8080"
            : urlText;
        _settings.PluginPairCode = _pairCodeBox.Text?.Trim() ?? string.Empty;
        _settings.ShowDeveloperSettingsMenu = _showDeveloperSettingsToggle.IsChecked == true;
        // 属性变更已由 Plugin.cs 的 PropertyChanged 订阅自动落盘，无需重复写 Settings.json。

        _hint.Text = "已保存。服务器地址与端口在重启 ClassIsland 后生效；配对码保存后可点击“测试服务器连接”立即尝试配对。";
    }

    private async void OnTestConnectionClick(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        _testConnectionButton.IsEnabled = false;
        _connectionTestHint.Text = "正在使用当前已生效的设置测试真实 WebSocket 连接……";
        try
        {
            var result = await _service.TestCloudConnectionAsync();
            ApplyCloudConnectionStatus(result.Status);
            _connectionTestHint.Text = result.Message;
        }
        catch (Exception ex)
        {
            // 非预期 UI 调用异常不泄漏凭据；连接客户端仍会把完整异常写入日志。
            _connectionTestHint.Text = $"测试失败：{ex.GetType().Name}，请查看 ClassIsland 日志。";
        }
        finally
        {
            _testConnectionButton.IsEnabled = true;
        }
    }

    private void OnPushScheduleClick(object? sender, RoutedEventArgs e)
    {
        var status = _service?.PushCurrentSchedule() ?? new ScheduleSyncStatus
        {
            Source = ScheduleSyncSource.Plugin,
            State = ScheduleSyncTaskState.Failed,
            Message = "RemoteCI 服务尚未启动，暂时无法推送课表",
        };
        ApplyScheduleSyncStatus(status);
    }

    private void OnScheduleSyncStatusChanged(ScheduleSyncStatus status) =>
        Dispatcher.UIThread.Post(() => ApplyScheduleSyncStatus(status));

    private void OnCloudConnectionStatusChanged(CloudConnectionStatus status) =>
        Dispatcher.UIThread.Post(() => ApplyCloudConnectionStatus(status));

    internal static string ConnectionStatusText(CloudConnectionStatus status) =>
        $"服务器状态：{status.Summary}";

    private void ApplyCloudConnectionStatus(CloudConnectionStatus status)
    {
        _connectionStatus.Text = ConnectionStatusText(status);
        _connectionError.Text = status.Error is null ? string.Empty : $"最近错误：{status.Error}";
        _connectionError.IsVisible = status.Error is not null;
    }

    internal static string ScheduleStatusText(ScheduleSyncStatus status) => status.State switch
    {
        ScheduleSyncTaskState.Running => $"{status.Message}，请勿重复操作。",
        ScheduleSyncTaskState.Completed => status.Message,
        ScheduleSyncTaskState.Failed => status.Message,
        ScheduleSyncTaskState.Busy => status.Message,
        _ => status.Message,
    };

    private void ApplyScheduleSyncStatus(ScheduleSyncStatus status)
    {
        _pushScheduleButton.IsEnabled = _service?.CurrentScheduleSyncStatus is null;
        _hint.Text = ScheduleStatusText(status);
    }
}
