using System.Collections;
using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ClassIslandSettingsTests
{
    // ---------- 协议口径 ----------

    [Theory]
    [InlineData(CommandKind.ReadClassIslandSettings)]
    [InlineData(CommandKind.SetClassIslandSettings)]
    public void SettingsCommands_RequireControlClassIsland(CommandKind command)
    {
        Assert.Equal(UserPermissions.ControlClassIsland, CommandPermissions.Required(command));
        Assert.True(CommandPermissions.IsServerOnly(command));
    }

    [Fact]
    public void SettingsCapabilities_AreOptInAndNamed()
    {
        // 新能力只进入当前版本能力列表，未声明能力的旧 V3 端不能远程读写设置。
        Assert.DoesNotContain(RemoteCiCapabilities.HostSettingsRead, RemoteCiCapabilities.Baseline);
        Assert.DoesNotContain(RemoteCiCapabilities.HostSettingsWrite, RemoteCiCapabilities.Baseline);
        Assert.Contains(RemoteCiCapabilities.HostSettingsRead, RemoteCiCapabilities.Current);
        Assert.Contains(RemoteCiCapabilities.HostSettingsWrite, RemoteCiCapabilities.Current);
        Assert.NotEqual("未知能力", RemoteCiCapabilities.ChineseName(RemoteCiCapabilities.HostSettingsRead));
        Assert.NotEqual("未知能力", RemoteCiCapabilities.ChineseName(RemoteCiCapabilities.HostSettingsWrite));
        Assert.Equal(RemoteCiCapabilities.HostSettingsRead,
            RemoteCiCapabilities.Required(CommandKind.ReadClassIslandSettings));
        Assert.Equal(RemoteCiCapabilities.HostSettingsWrite,
            RemoteCiCapabilities.Required(CommandKind.SetClassIslandSettings));
    }

    // ---------- 目录与值转换（模拟宿主 Settings 的桩对象） ----------

    /// <summary>与 ClassIsland Settings 属性形态一致的测试桩。</summary>
    public sealed class StubSettings
    {
        public bool IsNotificationEnabled { get; set; }
        public int Theme { get; set; }
        public double Scale { get; set; }
        public string SplashCustomText { get; set; } = "";
        public DateTime SingleWeekStartTime { get; set; }
        public Color PrimaryColor { get; set; }
        public ObservableCollection<string> ExcludedFullscreenWindow { get; set; } = ["explorer"];

        /// <summary>目录之外的属性：读取时跳过，写入时按“设备不可用”处理。</summary>
        public string UnknownProperty { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Options = JsonDefaults.Options;

    private static ClassIslandSettingDescriptor Descriptor(ClassIslandSettingType type, string key) => new()
    {
        Key = key,
        Type = type,
        Label = key,
        Page = "general",
        Options = type == ClassIslandSettingType.Enum
            ? [new ClassIslandSettingOption(0, "跟随系统"), new ClassIslandSettingOption(1, "明亮"), new ClassIslandSettingOption(2, "黑暗")]
            : null,
    };

    [Fact]
    public void ToJson_RoundTripsSimpleTypes()
    {
        var settings = new StubSettings
        {
            IsNotificationEnabled = true,
            Theme = 2,
            Scale = 1.25,
            SplashCustomText = "高一（3）班",
            SingleWeekStartTime = new DateTime(2026, 2, 23, 14, 30, 0),
        };

        Assert.True(ClassIslandSettingsService.ToJson(
            Descriptor(ClassIslandSettingType.Boolean, "IsNotificationEnabled"),
            settings.IsNotificationEnabled, Options)!.Value.GetBoolean());
        Assert.Equal(2, ClassIslandSettingsService.ToJson(
            Descriptor(ClassIslandSettingType.Enum, "Theme"), settings.Theme, Options)!.Value.GetInt32());
        Assert.Equal(1.25, ClassIslandSettingsService.ToJson(
            Descriptor(ClassIslandSettingType.Double, "Scale"), settings.Scale, Options)!.Value.GetDouble());
        Assert.Equal("高一（3）班", ClassIslandSettingsService.ToJson(
            Descriptor(ClassIslandSettingType.String, "SplashCustomText"), settings.SplashCustomText, Options)!.Value.GetString());
        Assert.Equal("2026-02-23", ClassIslandSettingsService.ToJson(
            Descriptor(ClassIslandSettingType.Date, "SingleWeekStartTime"), settings.SingleWeekStartTime, Options)!.Value.GetString());
    }

    [Fact]
    public void ToJson_ColorDropsOpaqueAlphaAndKeepsTransparency()
    {
        Assert.Equal("#276b5c", ClassIslandSettingsService.ToJson(
            Descriptor(ClassIslandSettingType.Color, "PrimaryColor"), Color.FromRgb(0x27, 0x6b, 0x5c), Options)!.Value.GetString());
        Assert.Equal("#80ff0000", ClassIslandSettingsService.ToJson(
            Descriptor(ClassIslandSettingType.Color, "PrimaryColor"),
            Color.FromArgb(0x80, 0xff, 0x00, 0x00), Options)!.Value.GetString());
    }

    [Fact]
    public void ToJson_StringListSerializesItems()
    {
        var json = ClassIslandSettingsService.ToJson(
            Descriptor(ClassIslandSettingType.StringList, "ExcludedFullscreenWindow"),
            new StubSettings().ExcludedFullscreenWindow, Options)!.Value;
        Assert.Equal(JsonValueKind.Array, json.ValueKind);
        Assert.Equal(["explorer"], json.EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void ToSettingValue_ParsesColorDateAndEnum()
    {
        var color = Assert.IsType<Color>(ClassIslandSettingsService.ToSettingValue(
            Descriptor(ClassIslandSettingType.Color, "PrimaryColor"), typeof(Color),
            JsonSerializer.SerializeToElement("#276b5c", Options), null));
        Assert.Equal(Color.FromRgb(0x27, 0x6b, 0x5c), color);

        var withAlpha = Assert.IsType<Color>(ClassIslandSettingsService.ToSettingValue(
            Descriptor(ClassIslandSettingType.Color, "PrimaryColor"), typeof(Color),
            JsonSerializer.SerializeToElement("#80FF0000", Options), null));
        Assert.Equal(Color.FromArgb(0x80, 0xff, 0x00, 0x00), withAlpha);

        var date = Assert.IsType<DateTime>(ClassIslandSettingsService.ToSettingValue(
            Descriptor(ClassIslandSettingType.Date, "SingleWeekStartTime"), typeof(DateTime),
            JsonSerializer.SerializeToElement("2026-02-23", Options), null));
        Assert.Equal(new DateTime(2026, 2, 23), date);

        var theme = ClassIslandSettingsService.ToSettingValue(
            Descriptor(ClassIslandSettingType.Enum, "Theme"), typeof(int),
            JsonSerializer.SerializeToElement(2.0, Options), null);
        Assert.Equal(2, theme);
    }

    [Fact]
    public void ToSettingValue_StringListMutatesExistingCollection()
    {
        // 宿主可能持有集合引用，写入必须原地改写而不是替换属性值。
        var list = new ObservableCollection<string> { "explorer", "game" };
        var result = Assert.IsAssignableFrom<IList>(ClassIslandSettingsService.ToSettingValue(
            Descriptor(ClassIslandSettingType.StringList, "ExcludedFullscreenWindow"),
            typeof(ObservableCollection<string>),
            JsonSerializer.SerializeToElement(new[] { "chrome" }, Options), list));
        Assert.Same(list, result);
        Assert.Equal(["chrome"], list.Cast<string>());
    }

    [Theory]
    [InlineData(ClassIslandSettingType.Boolean, "\"yes\"")]
    [InlineData(ClassIslandSettingType.Integer, "\"2\"")]
    [InlineData(ClassIslandSettingType.Double, "\"1.2\"")]
    [InlineData(ClassIslandSettingType.Color, "\"red\"")]
    [InlineData(ClassIslandSettingType.Date, "\"2026/02/23\"")]
    [InlineData(ClassIslandSettingType.StringList, "\"explorer\"")]
    public void ToSettingValue_RejectsMismatchedTypes(ClassIslandSettingType type, string json)
    {
        var propertyType = type switch
        {
            ClassIslandSettingType.Boolean => typeof(bool),
            ClassIslandSettingType.Integer or ClassIslandSettingType.Enum => typeof(int),
            ClassIslandSettingType.Double => typeof(double),
            ClassIslandSettingType.Color => typeof(Color),
            ClassIslandSettingType.Date => typeof(DateTime),
            _ => typeof(ObservableCollection<string>),
        };
        Assert.Null(ClassIslandSettingsService.ToSettingValue(
            Descriptor(type, "Any"), propertyType, JsonSerializer.Deserialize<JsonElement>(json), null));
    }

    [Fact]
    public async Task ApplyAsync_WithoutValuesReturnsInvalidRequest()
    {
        var service = new ClassIslandSettingsService(null, TestLogger.Instance);
        var result = await service.ApplyAsync(new ClassIslandSettingsRequest());
        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.InvalidRequest, result.Code);
    }

    [Fact]
    public async Task ApplyAsync_WithUnknownHostReturnsCapabilityUnsupported()
    {
        // 桩环境下解析不到宿主 SettingsService：应回明确的失败码，而不是误报成功。
        var values = new Dictionary<string, JsonElement> { ["Theme"] = JsonSerializer.SerializeToElement(1, Options) };
        var service = new ClassIslandSettingsService(null, TestLogger.Instance);
        var result = await service.ApplyAsync(new ClassIslandSettingsRequest { Values = values });
        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.CapabilityUnsupported, result.Code);
    }

    [Fact]
    public async Task ReadAsync_WithoutHostReturnsCapabilityUnsupported()
    {
        var service = new ClassIslandSettingsService(null, TestLogger.Instance);
        var result = await service.ReadAsync();
        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.CapabilityUnsupported, result.Code);
    }

    private sealed class TestLogger : Microsoft.Extensions.Logging.ILogger<ClassIslandSettingsService>
    {
        public static readonly TestLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}
