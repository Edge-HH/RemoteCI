using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace RemoteCI.Plugin.Settings;

/// <summary>
/// 插件配置模型，保存在 ClassIsland 插件配置目录的 Settings.json。
/// 修改任何属性后会自动触发保存（见 Plugin.cs）。
/// </summary>
public sealed class PluginSettings : INotifyPropertyChanged
{
    private bool _enableLanServer = true;
    private int _lanServerPort = 8765;
    private bool _enableCloud = true;
    private string _cloudServerUrl = "http://localhost:8080";
    private string _pluginPairCode = string.Empty;
    private string? _cloudToken;
    private bool _showDeveloperSettingsMenu;

    /// <summary>是否启用局域网直连服务（手表同 WiFi 直连插件）。</summary>
    public bool EnableLanServer
    {
        get => _enableLanServer;
        set => SetField(ref _enableLanServer, value);
    }

    /// <summary>局域网 WebSocket 服务端口。</summary>
    public int LanServerPort
    {
        get => _lanServerPort;
        set => SetField(ref _lanServerPort, value);
    }

    /// <summary>是否启用云端中转（经服务端与手表通信）。</summary>
    public bool EnableCloud
    {
        get => _enableCloud;
        set => SetField(ref _enableCloud, value);
    }

    /// <summary>云端服务端地址（如 http://nas:8080）。</summary>
    public string CloudServerUrl
    {
        get => _cloudServerUrl;
        set => SetField(ref _cloudServerUrl, value);
    }

    /// <summary>WebUI 生成的一次性插件配对码；学生账号不使用此值。</summary>
    public string PluginPairCode
    {
        get => _pluginPairCode;
        set => SetField(ref _pluginPairCode, value);
    }

    /// <summary>是否在 ClassIsland 设置导航中注册 RemoteCI 开发者设置页；默认隐藏。</summary>
    public bool ShowDeveloperSettingsMenu
    {
        get => _showDeveloperSettingsMenu;
        set => SetField(ref _showDeveloperSettingsMenu, value);
    }

    /// <summary>云端配对后缓存的长期插件凭据。仅存内存；持久化由 CloudTokenStore 用 DPAPI 加密落盘。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? CloudToken
    {
        get => _cloudToken;
        set => SetField(ref _cloudToken, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
