using System.Reflection;
using System.Text;
using System.Text.Json;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// ClassIsland 2.x 主界面文件适配器。主界面文件保存在 ComponentsService.ComponentSettingsPath
/// （Config/ComponentLayouts/），当前文件由设置项 CurrentComponentConfig 选择；
/// 修改该设置时宿主会先保存旧文件再加载新文件，与本地设置页切换主界面完全一致。
/// 宿主实现类型与私有成员均按名称反射访问，不在编译期绑定。
/// </summary>
internal sealed class ClassIslandMainWindowLayoutBackend(object service) : IMainWindowLayoutBackend
{
    private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public bool IsManagementMode => GetProperty(service, "IsManagementMode") is true;

    public string CurrentName => GetProperty(Settings, "CurrentComponentConfig") as string is { Length: > 0 } name
        ? name
        : throw new InvalidOperationException("宿主未提供当前主界面文件名");

    public IReadOnlyList<string> ListNames() => Directory.Exists(LayoutDirectory)
        ? Directory.GetFiles(LayoutDirectory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>()
            .Order(StringComparer.OrdinalIgnoreCase).ToList()
        : [];

    public string ReadCurrentJson()
    {
        // 集控模式下主界面来自集控缓存而非本地文件，直接序列化宿主内存中的组件配置。
        if (IsManagementMode) return JsonSerializer.Serialize(CurrentComponents, CurrentComponents.GetType());
        Invoke(service, "SaveConfig");
        var path = PathOf(CurrentName);
        return File.Exists(path) ? File.ReadAllText(path) : JsonSerializer.Serialize(CurrentComponents, CurrentComponents.GetType());
    }

    public void Validate(string json)
    {
        // ConfigureFileHelper 已把宿主序列化选项设为 JsonSerializer 默认值，与宿主加载文件时一致。
        var profile = JsonSerializer.Deserialize(json, CurrentComponents.GetType())
            ?? throw new InvalidOperationException("主界面文件为空");
        if (GetProperty(profile, "Lines") is null) throw new InvalidOperationException("缺少主界面行");
    }

    public bool Exists(string name) => File.Exists(PathOf(name));

    public byte[]? ReadFile(string name) => File.Exists(PathOf(name)) ? File.ReadAllBytes(PathOf(name)) : null;

    public void WriteFile(string name, string json, bool createNew)
    {
        Directory.CreateDirectory(LayoutDirectory);
        // CreateNew 关闭检查与写入之间的竞态，新建时绝不覆盖本地已有文件。
        using var stream = new FileStream(PathOf(name), createNew ? FileMode.CreateNew : FileMode.Create,
            FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(json);
    }

    public void RestoreFile(string name, byte[]? content)
    {
        var path = PathOf(name);
        if (content is not null) File.WriteAllBytes(path, content);
        else if (File.Exists(path)) File.Delete(path);
    }

    public void ReloadCurrent()
    {
        var load = service.GetType().GetMethod("LoadConfig", InstanceFlags, binder: null, Type.EmptyTypes, modifiers: null)
            ?? throw new InvalidOperationException("当前 ClassIsland 版本不支持重新加载主界面");
        load.Invoke(service, null);
    }

    public void Activate(string name)
    {
        RefreshList();
        HostApiCompat.WriteProperty(Settings, "CurrentComponentConfig", name);
        SaveSettings();
    }

    public void RefreshList() => Invoke(service, "RefreshConfigs");

    private object CurrentComponents => GetProperty(service, "CurrentComponents")
        ?? throw new InvalidOperationException("宿主未提供当前主界面");

    private string LayoutDirectory
    {
        get
        {
            var type = service.GetType();
            return type.GetField("ComponentSettingsPath", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string
                ?? type.GetProperty("ComponentSettingsPath", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string
                ?? throw new InvalidOperationException("宿主未提供主界面文件目录");
        }
    }

    private string PathOf(string name)
    {
        var directory = Path.GetFullPath(LayoutDirectory);
        var path = Path.GetFullPath(Path.Combine(directory, name + ".json"));
        // 名称已在协议层校验；这里再确认不会离开主界面文件目录。
        if (!string.Equals(Path.GetDirectoryName(path), directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("主界面文件名无效");
        return path;
    }

    private object SettingsService => GetProperty(service, "SettingsService")
        ?? throw new InvalidOperationException("宿主未提供主界面设置");
    private object Settings => GetProperty(SettingsService, "Settings")
        ?? throw new InvalidOperationException("宿主设置不存在");

    private void SaveSettings()
    {
        var settings = SettingsService;
        // 与档案切换相同：ClassIsland 2.1 只有 SaveSettings(string note)，无参形式留给其他宿主版本。
        if (settings.GetType().GetMethod("SaveSettings", InstanceFlags, binder: null, types: [typeof(string)], modifiers: null) is { } withNote)
            withNote.Invoke(settings, ["RemoteCI 切换主界面文件"]);
        else
            (settings.GetType().GetMethod("SaveSettings", InstanceFlags, binder: null, types: Type.EmptyTypes, modifiers: null)
                ?? throw new InvalidOperationException("宿主未提供设置保存接口")).Invoke(settings, null);
    }

    private static void Invoke(object source, string method) =>
        (source.GetType().GetMethod(method, InstanceFlags, binder: null, Type.EmptyTypes, modifiers: null)
            ?? throw new InvalidOperationException($"宿主未提供 {method}")).Invoke(source, null);

    private static object? GetProperty(object source, string name) =>
        source.GetType().GetProperty(name, InstanceFlags)?.GetValue(source);
}
