using System.Reflection;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassIsland.Shared.Models.Profile;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// ClassIsland 2.0–2.2 的档案事务适配器。通过运行时属性访问字典，避免旧 SDK getter 的二进制签名。
/// 当前档案保留根对象身份和宿主订阅；创建档案才切换根对象及选中路径。
/// </summary>
internal sealed class ClassIslandProfileApplicationBackend(object service) : IProfileApplicationBackend
{
    // 预定课表与临时课表指针随课表一起写入，候选中已清除的悬空引用才会真正离开宿主档案。
    private static readonly string[] DictionaryProperties = ["Subjects", "TimeLayouts", "ClassPlans", "ClassPlanGroups",
        "OrderedSchedules", "TempClassPlanId", "OverlayClassPlanId"];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private string? _createdPath;
    private object? _createdProfile;
    private System.ComponentModel.PropertyChangedEventHandler? _createdSaveHandler;

    private sealed record Snapshot(Profile Profile, Dictionary<string, object?> Dictionaries,
        string CurrentPath, object? SelectedPath, bool? Trusted, string FilePath, byte[]? FileBytes);

    private Profile CurrentProfile => HostApiCompat.ReadProperty<Profile>(service, "Profile");
    public string ReadCurrentJson() => JsonSerializer.Serialize(CurrentProfile, JsonOptions);
    public bool ProfileExists(string name) => File.Exists(Path.Combine(ProfileDirectory, name + ".json"));

    public object CaptureState()
    {
        var currentPath = ReadCurrentPath();
        var filePath = Path.Combine(ProfileDirectory, Path.GetFileName(currentPath));
        return new Snapshot(CurrentProfile, DictionaryProperties.ToDictionary(name => name,
                name => GetProperty(CurrentProfile, name)), currentPath, ReadSelectedPath(),
            GetProperty(service, "IsCurrentProfileTrusted") is bool trusted ? trusted : null, filePath,
            File.Exists(filePath) ? File.ReadAllBytes(filePath) : null);
    }

    public void ApplyCurrentJson(string json)
    {
        var candidate = Deserialize(json);
        var target = CurrentProfile;
        foreach (var name in DictionaryProperties)
            HostApiCompat.WriteProperty(target, name, GetProperty(candidate, name));
    }

    public void CreateAndActivate(string json, string name)
    {
        var candidate = Deserialize(json);
        candidate.Name = name;
        candidate.Id = Guid.NewGuid();
        var filename = name + ".json";
        var path = Path.Combine(ProfileDirectory, filename);
        // CreateNew closes the check/write race and never overwrites an existing local profile.
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        _createdPath = path;
        // Trust is cached by ProfileService and normally recalculated by LoadProfileAsync.
        // A new random profile ID has never been locally trusted; do not inherit the old cache.
        WriteTrustedState(false);
        HostApiCompat.WriteProperty(service, "Profile", candidate);
        HostApiCompat.WriteProperty(service, "CurrentProfilePath", filename);
        WriteSelectedPath(filename);
        // LoadProfileAsync ordinarily installs this subscription. The remote creation path must
        // install it too so later local edits still use the host's normal automatic persistence.
        _createdProfile = candidate;
        _createdSaveHandler = (_, _) => SaveCurrentProfile();
        candidate.PropertyChanged += _createdSaveHandler;
    }

    public void Save()
    {
        SaveCurrentProfile();
        if (_createdPath is not null) SaveSettings();
    }

    public void RestoreState(object snapshot)
    {
        var state = (Snapshot)snapshot;
        if (_createdProfile is Profile created && _createdSaveHandler is not null)
            created.PropertyChanged -= _createdSaveHandler;
        Exception? restoreError = null;
        try
        {
            HostApiCompat.WriteProperty(service, "Profile", state.Profile);
            HostApiCompat.WriteProperty(service, "CurrentProfilePath", state.CurrentPath);
            foreach (var (name, value) in state.Dictionaries)
            {
                // Root PropertyChanged can auto-save; keep restoring the remaining dictionaries
                // even if the disk is unavailable during one of those notifications.
                try { HostApiCompat.WriteProperty(state.Profile, name, value); }
                catch (Exception ex) { restoreError ??= ex; }
            }
            WriteSelectedPath(state.SelectedPath);
            if (state.Trusted is { } trusted) WriteTrustedState(trusted);
            if (_createdPath is not null) SaveSettings();
        }
        finally
        {
            if (state.FileBytes is not null) File.WriteAllBytes(state.FilePath, state.FileBytes);
            else if (File.Exists(state.FilePath)) File.Delete(state.FilePath);
            if (_createdPath is not null && File.Exists(_createdPath)) File.Delete(_createdPath);
        }
        if (restoreError is not null) throw restoreError;
    }

    private static Profile Deserialize(string json)
    {
        var document = ProfileDocument.Parse(json);
        if (ReadNode(document, "TimeLayouts") is JsonObject layouts)
            foreach (var layout in layouts.Select(entry => entry.Value).OfType<JsonObject>())
                if (ReadNode(layout, "Layouts") is JsonArray points)
                    foreach (var point in points.OfType<JsonObject>())
                        foreach (var (field, legacyField) in new[] { ("StartTime", "StartSecond"), ("EndTime", "EndSecond") })
                        {
                            if (ReadNode(point, legacyField) is not JsonValue legacy) continue;
                            // 旧集控导出可能使用 DateTime 或秒数；SDK 2.x 的兼容属性只接受秒数字符串。
                            // 只转换宿主的候选副本，服务端仍保留上传文件的完整字段和原始格式。
                            if (ReadNode(point, field) is null && ProfileDocument.TryReadTime(point, field == "StartTime", out var time))
                            {
                                point[field] = time.ToString("c", CultureInfo.InvariantCulture);
                                var key = point.First(entry => entry.Key.Equals(legacyField, StringComparison.OrdinalIgnoreCase)).Key;
                                point[key] = time.TotalSeconds.ToString(CultureInfo.InvariantCulture);
                            }
                            else if (!legacy.TryGetValue<string>(out _))
                            {
                                var key = point.First(entry => entry.Key.Equals(legacyField, StringComparison.OrdinalIgnoreCase)).Key;
                                point[key] = legacy.ToJsonString();
                            }
                        }
        return JsonSerializer.Deserialize<Profile>(ProfileDocument.Serialize(document), JsonOptions)
            ?? throw new InvalidOperationException("宿主无法解析档案 JSON");
    }

    private static JsonNode? ReadNode(JsonObject node, string field) =>
        node.FirstOrDefault(entry => entry.Key.Equals(field, StringComparison.OrdinalIgnoreCase)).Value;

    private string ProfileDirectory
    {
        get
        {
            var type = service.GetType();
            // ProfilePath is a static readonly field in stable ClassIsland; some hosts expose
            // a property. Both forms are valid, neither is bound at compile time.
            return type.GetProperty("ProfilePath", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string
                ?? type.GetField("ProfilePath", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string
                ?? throw new InvalidOperationException("宿主未提供档案目录");
        }
    }

    private object SettingsService => service.GetType().GetProperty("SettingsService",
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(service)
        ?? throw new InvalidOperationException("宿主未提供档案选择设置");
    private object Settings => GetProperty(SettingsService, "Settings")
        ?? throw new InvalidOperationException("宿主设置不存在");
    private object? ReadSelectedPath() => GetProperty(Settings, "SelectedProfile");
    private void WriteSelectedPath(object? value) => HostApiCompat.WriteProperty(Settings, "SelectedProfile", value);
    private void WriteTrustedState(bool trusted)
    {
        var property = service.GetType().GetProperty("IsCurrentProfileTrusted", BindingFlags.Public | BindingFlags.Instance);
        if (property?.CanWrite == true) property.SetValue(service, trusted);
    }
    private string ReadCurrentPath() => GetProperty(service, "CurrentProfilePath") as string
        ?? throw new InvalidOperationException("宿主未提供当前档案路径");
    private void SaveCurrentProfile()
    {
        // SaveProfile(string) always persists. The parameterless overload silently returns
        // before host initialization, which would otherwise produce a false success receipt.
        var method = service.GetType().GetMethod("SaveProfile", [typeof(string)])
            ?? throw new InvalidOperationException("宿主未提供档案保存接口");
        method.Invoke(service, [Path.GetFileName(ReadCurrentPath())]);
    }
    private void SaveSettings()
    {
        var settings = SettingsService;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        // ClassIsland 2.1 只有 SaveSettings(string note)，note 仅用于宿主日志；无参形式留给其他宿主版本。
        if (settings.GetType().GetMethod("SaveSettings", flags, binder: null, types: [typeof(string)], modifiers: null) is { } withNote)
            withNote.Invoke(settings, ["RemoteCI 切换档案"]);
        else
            (settings.GetType().GetMethod("SaveSettings", flags, binder: null, types: Type.EmptyTypes, modifiers: null)
                ?? throw new InvalidOperationException("宿主未提供设置保存接口")).Invoke(settings, null);
    }
    private static object? GetProperty(object source, string name) => source.GetType().GetProperty(name,
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(source);
}
