using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Text.Json;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class MainWindowLayoutTests
{
    private const string LayoutJson = """{"Lines":[{"Children":[{"Id":"df3f8295-21f6-482e-bada-fa0e5f14bb66"}]}]}""";

    private sealed class FakeBackend : IMainWindowLayoutBackend
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase) { ["Default"] = "{\"Lines\":[]}" };
        public bool IsManagementMode { get; set; }
        public string CurrentName { get; set; } = "Default";
        public bool FailReload { get; set; }
        public bool FailActivate { get; set; }
        public int Reloads { get; private set; }

        public IReadOnlyList<string> ListNames() => Files.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        public string ReadCurrentJson() => Files[CurrentName];
        public void Validate(string json) => JsonDocument.Parse(json).Dispose();
        public bool Exists(string name) => Files.ContainsKey(name);
        public byte[]? ReadFile(string name) => Files.TryGetValue(name, out var json) ? Encoding.UTF8.GetBytes(json) : null;

        public void WriteFile(string name, string json, bool createNew)
        {
            if (createNew && Files.ContainsKey(name)) throw new IOException("exists");
            Files[name] = json;
        }

        public void RestoreFile(string name, byte[]? content)
        {
            if (content is null) Files.Remove(name);
            else Files[name] = Encoding.UTF8.GetString(content);
        }

        public void ReloadCurrent()
        {
            Reloads++;
            if (FailReload && Reloads == 1) throw new InvalidOperationException("reload failed");
        }

        public void Activate(string name)
        {
            CurrentName = name;
            if (FailActivate && name != "Default") throw new InvalidOperationException("activate failed");
        }

        public void RefreshList() { }
    }

    private static MainWindowLayoutApplyRequest Request(MainWindowLayoutApplyMode mode, string? name = null, bool activate = true) =>
        new() { LayoutJson = LayoutJson, Mode = mode, Name = name, Activate = activate };

    [Fact]
    public void OverwriteReplacesCurrentFileAndReloads()
    {
        var backend = new FakeBackend();
        var result = MainWindowLayoutExecutor.Apply(Request(MainWindowLayoutApplyMode.OverwriteCurrent), backend);

        Assert.True(result.Success, result.Message);
        Assert.Equal(LayoutJson, backend.Files["Default"]);
        Assert.Equal(1, backend.Reloads);
    }

    [Fact]
    public void OverwriteRestoresOriginalWhenReloadFails()
    {
        var backend = new FakeBackend { FailReload = true };
        var result = MainWindowLayoutExecutor.Apply(Request(MainWindowLayoutApplyMode.OverwriteCurrent), backend);

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.SaveFailed, result.Code);
        Assert.Equal("{\"Lines\":[]}", backend.Files["Default"]);
        Assert.Equal(2, backend.Reloads);
    }

    [Fact]
    public void CreateNewActivatesNewFile()
    {
        var backend = new FakeBackend();
        var result = MainWindowLayoutExecutor.Apply(Request(MainWindowLayoutApplyMode.CreateNew, " 考试.json "), backend);

        Assert.True(result.Success, result.Message);
        Assert.Equal("考试", backend.CurrentName);
        Assert.Equal(LayoutJson, backend.Files["考试"]);
        Assert.Equal("{\"Lines\":[]}", backend.Files["Default"]);
    }

    [Fact]
    public void CreateNewWithoutActivateKeepsCurrentLayout()
    {
        var backend = new FakeBackend();
        var result = MainWindowLayoutExecutor.Apply(Request(MainWindowLayoutApplyMode.CreateNew, "备用", activate: false), backend);

        Assert.True(result.Success, result.Message);
        Assert.Equal("Default", backend.CurrentName);
        Assert.True(backend.Files.ContainsKey("备用"));
    }

    [Fact]
    public void CreateNewNeverOverwritesExistingFile()
    {
        var backend = new FakeBackend();
        var result = MainWindowLayoutExecutor.Apply(Request(MainWindowLayoutApplyMode.CreateNew, "default"), backend);

        Assert.False(result.Success);
        Assert.Contains("已存在", result.Message);
        Assert.Equal("{\"Lines\":[]}", backend.Files["Default"]);
    }

    [Fact]
    public void CreateNewRollsBackWhenActivationFails()
    {
        var backend = new FakeBackend { FailActivate = true };
        var result = MainWindowLayoutExecutor.Apply(Request(MainWindowLayoutApplyMode.CreateNew, "新界面"), backend);

        Assert.False(result.Success);
        Assert.Equal("Default", backend.CurrentName);
        Assert.False(backend.Files.ContainsKey("新界面"));
    }

    [Fact]
    public void ManagementModeRejectsUpload()
    {
        var backend = new FakeBackend { IsManagementMode = true };
        var result = MainWindowLayoutExecutor.Apply(Request(MainWindowLayoutApplyMode.OverwriteCurrent), backend);

        Assert.False(result.Success);
        Assert.Contains("集控", result.Message);
        Assert.Equal("{\"Lines\":[]}", backend.Files["Default"]);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"Name\":\"x\"}")]
    [InlineData("not json")]
    [InlineData("")]
    public void RejectsFilesThatAreNotMainWindowLayouts(string json)
    {
        var request = Request(MainWindowLayoutApplyMode.OverwriteCurrent);
        request.LayoutJson = json;
        var result = MainWindowLayoutExecutor.Apply(request, new FakeBackend());

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.InvalidRequest, result.Code);
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a\\b")]
    [InlineData("名称.")]
    [InlineData("  ")]
    [InlineData(null)]
    public void RejectsUnsafeNewNames(string? name) =>
        Assert.NotNull(MainWindowLayoutRules.Validate(Request(MainWindowLayoutApplyMode.CreateNew, name)));

    [Fact]
    public void ReadReturnsCurrentFileWithList()
    {
        var backend = new FakeBackend();
        backend.Files["备用"] = LayoutJson;
        var result = MainWindowLayoutExecutor.Read(backend);

        Assert.True(result.Success, result.Message);
        var snapshot = JsonSerializer.Deserialize<MainWindowLayoutSnapshot>(result.Data!)!;
        Assert.Equal("Default", snapshot.Name);
        Assert.Equal("{\"Lines\":[]}", snapshot.LayoutJson);
        Assert.Equal(["Default", "备用"], snapshot.Layouts);
    }

    [Fact]
    public void CapabilitiesAndPermissionsAreDeclared()
    {
        foreach (var capability in new[] { RemoteCiCapabilities.MainWindowLayoutApply, RemoteCiCapabilities.MainWindowLayoutRead })
        {
            Assert.DoesNotContain(capability, RemoteCiCapabilities.Baseline);
            Assert.Contains(capability, RemoteCiCapabilities.Current);
        }
        Assert.Equal(RemoteCiCapabilities.MainWindowLayoutApply, RemoteCiCapabilities.Required(CommandKind.ApplyMainWindowLayout));
        Assert.Equal(RemoteCiCapabilities.MainWindowLayoutRead, RemoteCiCapabilities.Required(CommandKind.ReadMainWindowLayout));
        Assert.Equal(UserPermissions.MainMenuControl, CommandPermissions.Required(CommandKind.ApplyMainWindowLayout));
        Assert.Equal(UserPermissions.MainMenuControl, CommandPermissions.Required(CommandKind.ReadMainWindowLayout));
    }

    // ---------- 宿主适配器：按 ClassIsland 2.1 ComponentsService 的成员名模拟 ----------

    public sealed class FakeProfile
    {
        public ObservableCollection<FakeLine> Lines { get; set; } = [];
    }

    public sealed class FakeLine
    {
        public List<JsonElement> Children { get; set; } = [];
    }

    public sealed class FakeSettings : INotifyPropertyChanged
    {
        private string _current = "Default";
        public event PropertyChangedEventHandler? PropertyChanged;

        public string CurrentComponentConfig
        {
            get => _current;
            set { _current = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentComponentConfig))); }
        }
    }

    public sealed class FakeSettingsService
    {
        public FakeSettings Settings { get; } = new();
        public int Saves { get; private set; }
        public void SaveSettings(string note) => Saves++;
    }

    public sealed class FakeComponentsService
    {
        public static string ComponentSettingsPath = string.Empty;
        private string _loadedName = "Default";

        public FakeComponentsService()
        {
            SettingsService.Settings.PropertyChanged += (_, _) => { SaveConfig(); LoadConfig(); };
            LoadConfig();
        }

        private FakeSettingsService SettingsService { get; } = new();
        public FakeSettingsService ExposedSettingsService => SettingsService;
        public bool IsManagementMode { get; set; }
        public FakeProfile CurrentComponents { get; set; } = new();
        public int Refreshes { get; private set; }

        private string PathOf(string name) => Path.Combine(ComponentSettingsPath, name + ".json");

        private void LoadConfig()
        {
            var path = PathOf(SettingsService.Settings.CurrentComponentConfig);
            CurrentComponents = File.Exists(path) ? JsonSerializer.Deserialize<FakeProfile>(File.ReadAllText(path))! : new FakeProfile();
            _loadedName = SettingsService.Settings.CurrentComponentConfig;
        }

        public void SaveConfig() => File.WriteAllText(PathOf(_loadedName), JsonSerializer.Serialize(CurrentComponents));
        public void RefreshConfigs() => Refreshes++;
    }

    [Fact]
    public void HostAdapterOverwritesCreatesAndSwitchesLayouts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "remoteci-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            FakeComponentsService.ComponentSettingsPath = directory + Path.DirectorySeparatorChar;
            File.WriteAllText(Path.Combine(directory, "Default.json"), "{\"Lines\":[]}");
            var service = new FakeComponentsService();
            var backend = new ClassIslandMainWindowLayoutBackend(service);

            var overwrite = MainWindowLayoutExecutor.Apply(Request(MainWindowLayoutApplyMode.OverwriteCurrent), backend);
            Assert.True(overwrite.Success, overwrite.Message);
            Assert.Single(service.CurrentComponents.Lines);

            var created = MainWindowLayoutExecutor.Apply(new MainWindowLayoutApplyRequest
            {
                LayoutJson = """{"Lines":[{"Children":[]},{"Children":[]}]}""",
                Mode = MainWindowLayoutApplyMode.CreateNew,
                Name = "考试",
            }, backend);
            Assert.True(created.Success, created.Message);
            Assert.Equal("考试", service.ExposedSettingsService.Settings.CurrentComponentConfig);
            Assert.Equal(2, service.CurrentComponents.Lines.Count);
            Assert.Equal(1, service.ExposedSettingsService.Saves);
            // 切换前宿主把旧主界面落盘，覆盖内容保留在 Default.json。
            Assert.Contains("df3f8295", File.ReadAllText(Path.Combine(directory, "Default.json")));

            var read = MainWindowLayoutExecutor.Read(backend);
            Assert.True(read.Success, read.Message);
            var snapshot = JsonSerializer.Deserialize<MainWindowLayoutSnapshot>(read.Data!)!;
            Assert.Equal("考试", snapshot.Name);
            Assert.Equal(["Default", "考试"], snapshot.Layouts);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
