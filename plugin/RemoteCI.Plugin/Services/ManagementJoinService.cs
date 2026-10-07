using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services.Management;
using ClassIsland.Shared;
using ClassIsland.Shared.Enums;
using ClassIsland.Shared.Models.Management;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 批量加入 ClassIsland 内置集控。输入为管理员上传的集控配置文件（ManagementPreset.json），
/// 实现复刻宿主 JoinManagementAsync 的无交互路径：先注册/下载清单，再写入宿主的 Management
/// 配置，最后重启让宿主完成档案拉取。集控 ID（ClassIdentity）由服务端按设备班级名自动填充。
/// </summary>
public sealed class ManagementJoinService(ILogger<ManagementJoinService> logger)
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private static readonly JsonSerializerOptions PresetJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<CommandResult> JoinAsync(ManagementJoinRequest? request, CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PresetJson))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "缺少集控配置文件");
        var management = IAppHost.TryGetService<IManagementService>();
        if (management is null)
            return CommandResult.Failure(CommandResultCodes.CapabilityUnsupported, "当前 ClassIsland 版本不支持集控");
        if (management.IsManagementEnabled)
            return CommandResult.Failure(CommandResultCodes.Busy, "设备已加入集控，不能重复加入");

        ManagementSettings settings;
        try
        {
            settings = ParseSettings(request);
        }
        catch (JsonException ex)
        {
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, $"集控配置文件格式无效：{ex.Message}");
        }
        if (!IsSettingsComplete(settings))
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "集控配置文件缺少服务器地址");

        ManagementManifest? manifest = null;

        var managementType = Type.GetType("ClassIsland.Services.Management.ManagementService, ClassIsland", true)!;
        Directory.CreateDirectory(GetStaticString(managementType, "ManagementConfigureFolderPath"));

        try
        {
            manifest = await ResolveManifestAsync(settings, management.Persist.ClientUniqueId, ct);
            if (manifest.CoreVersion.Major != IAppHost.CoreVersion.Major)
                return CommandResult.Failure(
                    CommandResultCodes.CapabilityUnsupported,
                    $"集控核心版本 {manifest.CoreVersion} 与当前 ClassIsland {IAppHost.CoreVersion} 不兼容");

            await WriteManagementFilesAsync(settings, manifest, management.Persist.ClientUniqueId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "加入 ClassIsland 集控失败");
            return CommandResult.Failure(CommandResultCodes.InternalError, $"加入集控失败：{ex.Message}");
        }

        ScheduleRestart();
        return Success($"已加入组织 {manifest!.OrganizationName}，ClassIsland 将自动重启");
    }

    /// <summary>
    /// 解析管理员上传的集控配置文件（ClassIsland 的 ManagementPreset.json，即 ManagementSettings 的 JSON 序列化）。
    /// 加入集控即视为启用；ID（ClassIdentity）统一使用服务端按设备班级名自动下发的值。
    /// </summary>
    private static ManagementSettings ParseSettings(ManagementJoinRequest request)
    {
        var settings = JsonSerializer.Deserialize<ManagementSettings>(request.PresetJson, PresetJsonOptions)
            ?? throw new JsonException("集控配置文件为空");
        settings.IsManagementEnabled = true;
        settings.ManagementServer = settings.ManagementServer?.Trim() ?? string.Empty;
        settings.ManagementServerGrpc = settings.ManagementServerGrpc?.Trim() ?? string.Empty;
        settings.ManifestUrlTemplate = settings.ManifestUrlTemplate?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(request.ClassIdentity))
            settings.ClassIdentity = request.ClassIdentity.Trim();
        return settings;
    }

    private static bool IsSettingsComplete(ManagementSettings settings) =>
        settings.ManagementServerKind == ManagementServerKind.ManagementServer
            ? Uri.TryCreate(settings.ManagementServer, UriKind.Absolute, out _) &&
              Uri.TryCreate(settings.ManagementServerGrpc, UriKind.Absolute, out _)
            : Uri.TryCreate(settings.ManifestUrlTemplate, UriKind.Absolute, out _);

    private static async Task<ManagementManifest> ResolveManifestAsync(
        ManagementSettings settings,
        Guid clientId,
        CancellationToken ct)
    {
        if (settings.ManagementServerKind == ManagementServerKind.Serverless)
        {
            var url = DecorateUrl(settings.ManifestUrlTemplate, clientId, settings.ClassIdentity, settings.ManagementServer);
            return await GetJsonAsync<ManagementManifest>(url, ct)
                ?? throw new InvalidOperationException("集控清单为空");
        }

        var connectionType = Type.GetType(
            "ClassIsland.Services.Management.ManagementServerConnection, ClassIsland",
            throwOnError: false)
            ?? throw new InvalidOperationException("找不到 ClassIsland 集控连接实现");
        var connection = Activator.CreateInstance(
            connectionType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [settings, clientId, true],
            culture: null) ?? throw new InvalidOperationException("无法创建 ClassIsland 集控连接");
        var register = connectionType.GetMethod("RegisterAsync", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new MissingMethodException(connectionType.FullName, "RegisterAsync");
        var task = register.Invoke(connection, null) as Task<ManagementManifest>
            ?? throw new InvalidOperationException("ClassIsland 集控注册接口返回了未知结果");
        return await task.WaitAsync(ct);
    }

    private static async Task WriteManagementFilesAsync(
        ManagementSettings settings,
        ManagementManifest manifest,
        Guid clientId,
        CancellationToken ct)
    {
        var managementType = Type.GetType("ClassIsland.Services.Management.ManagementService, ClassIsland", true)!;
        var profileType = Type.GetType("ClassIsland.Services.ProfileService, ClassIsland", true)!;
        var folder = GetStaticString(managementType, "ManagementConfigureFolderPath");
        Directory.CreateDirectory(folder);

        var paths = new[]
        {
            GetStaticString(managementType, "ManagementManifestPath"),
            GetStaticString(managementType, "ManagementPolicyPath"),
            GetStaticString(managementType, "ManagementVersionsPath"),
            GetStaticString(managementType, "ManagementCredentialsPath"),
            GetStaticString(profileType, "ManagementClassPlanPath"),
            GetStaticString(profileType, "ManagementSubjectsPath"),
            GetStaticString(profileType, "ManagementTimeLayoutPath"),
            Path.Combine(GetStaticString(profileType, "ProfilePath"), "_management-profile.json"),
        };
        foreach (var path in paths.Where(File.Exists))
        {
            File.Delete(path);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        }

        await WriteJsonAsync(GetStaticString(managementType, "ManagementManifestPath"), manifest, ct);
        await WriteJsonAsync(GetStaticString(managementType, "ManagementVersionsPath"), new ManagementVersions(), ct);
        await DownloadJsonIfPresentAsync<ManagementPolicy>(
            manifest.PolicySource.Value,
            GetStaticString(managementType, "ManagementPolicyPath"),
            settings,
            clientId,
            ct);
        await DownloadJsonIfPresentAsync<ManagementCredentialConfig>(
            manifest.CredentialSource.Value,
            GetStaticString(managementType, "ManagementCredentialsPath"),
            settings,
            clientId,
            ct);
        await WriteJsonAsync(GetStaticString(managementType, "ManagementSettingsPath"), settings, ct);
    }

    private static async Task DownloadJsonIfPresentAsync<T>(
        string? source,
        string destination,
        ManagementSettings settings,
        Guid clientId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        var url = DecorateUrl(source, clientId, settings.ClassIdentity, settings.ManagementServer);
        var json = await Http.GetStringAsync(url, ct);
        var value = JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("集控配置为空");
        await WriteJsonAsync(destination, value, ct);
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        });
        await File.WriteAllTextAsync(path, json, ct);
    }

    private static async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct)
    {
        var json = await Http.GetStringAsync(url, ct);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private static string GetStaticString(Type type, string name) =>
        type.GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string
        ?? throw new MissingFieldException(type.FullName, name);

    private static string DecorateUrl(string url, Guid clientId, string? classIdentity, string host) => url
        .Replace("{cuid}", clientId.ToString(), StringComparison.OrdinalIgnoreCase)
        .Replace("{id}", classIdentity ?? string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("{host}", host, StringComparison.OrdinalIgnoreCase);

    private static void ScheduleRestart()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            Dispatcher.UIThread.Post(() => AppBase.Current.Restart());
        });
    }

    private static CommandResult Success(string message) => new()
    {
        Success = true,
        Code = CommandResultCodes.Ok,
        Message = message,
    };
}





