using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>宿主主界面文件（组件配置方案）的最小读写面，便于脱离 ClassIsland 测试执行流程。</summary>
internal interface IMainWindowLayoutBackend
{
    bool IsManagementMode { get; }
    /// <summary>当前使用的主界面文件名（不含 .json）。</summary>
    string CurrentName { get; }
    IReadOnlyList<string> ListNames();
    /// <summary>当前主界面的 JSON；先把宿主内存中的修改落盘，读取结果与本地设置页一致。</summary>
    string ReadCurrentJson();
    /// <summary>用宿主的组件配置类型反序列化，拒绝宿主无法加载的文件。</summary>
    void Validate(string json);
    bool Exists(string name);
    byte[]? ReadFile(string name);
    /// <summary>写入主界面文件；createNew 为 true 时文件已存在即失败，绝不覆盖。</summary>
    void WriteFile(string name, string json, bool createNew);
    void RestoreFile(string name, byte[]? content);
    /// <summary>从磁盘重新加载当前主界面文件，使覆盖立即生效。</summary>
    void ReloadCurrent();
    /// <summary>切换宿主使用的主界面文件并保存设置。</summary>
    void Activate(string name);
    /// <summary>刷新宿主的主界面文件列表（新建但不使用时）。</summary>
    void RefreshList();
}

/// <summary>主界面文件上传与读取：覆盖失败时恢复原文件，新建失败时删除新文件并切回原主界面。</summary>
internal static class MainWindowLayoutExecutor
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new();

    internal static CommandResult Apply(MainWindowLayoutApplyRequest? request, IMainWindowLayoutBackend backend,
        Action<Exception>? logError = null)
    {
        if (MainWindowLayoutRules.Validate(request) is { } error)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, error);
        var valid = request!;
        if (backend.IsManagementMode)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "设备处于 ClassIsland 集控模式，主界面由集控管理，无法上传主界面文件");
        try { backend.Validate(valid.LayoutJson); }
        catch (Exception ex)
        {
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, $"ClassIsland 无法加载该主界面文件：{ex.Message}");
        }

        return valid.Mode == MainWindowLayoutApplyMode.OverwriteCurrent
            ? Overwrite(valid, backend, logError)
            : CreateNew(valid, backend, logError);
    }

    private static CommandResult Overwrite(MainWindowLayoutApplyRequest request, IMainWindowLayoutBackend backend,
        Action<Exception>? logError)
    {
        string name;
        byte[]? original;
        try
        {
            name = backend.CurrentName;
            original = backend.ReadFile(name);
        }
        catch (Exception ex)
        {
            logError?.Invoke(ex);
            return CommandResult.Failure(CommandResultCodes.InternalError, $"无法读取当前主界面文件：{ex.Message}");
        }

        try
        {
            backend.WriteFile(name, request.LayoutJson, createNew: false);
            backend.ReloadCurrent();
        }
        catch (Exception ex)
        {
            logError?.Invoke(ex);
            try
            {
                backend.RestoreFile(name, original);
                backend.ReloadCurrent();
            }
            catch (Exception restoreError) { logError?.Invoke(restoreError); }
            return CommandResult.Failure(CommandResultCodes.SaveFailed, $"覆盖主界面文件失败，已恢复原文件：{ex.Message}");
        }
        return Success($"已覆盖并使用主界面文件“{name}”");
    }

    private static CommandResult CreateNew(MainWindowLayoutApplyRequest request, IMainWindowLayoutBackend backend,
        Action<Exception>? logError)
    {
        var name = request.Name!;
        string previous;
        try
        {
            if (backend.Exists(name))
                return CommandResult.Failure(CommandResultCodes.InvalidRequest, $"主界面文件“{name}”已存在，请更换名称或选择覆盖");
            previous = backend.CurrentName;
            backend.WriteFile(name, request.LayoutJson, createNew: true);
        }
        catch (Exception ex)
        {
            logError?.Invoke(ex);
            return CommandResult.Failure(CommandResultCodes.SaveFailed, $"新建主界面文件失败：{ex.Message}");
        }

        try
        {
            if (request.Activate) backend.Activate(name);
            else backend.RefreshList();
        }
        catch (Exception ex)
        {
            logError?.Invoke(ex);
            try
            {
                if (request.Activate && !string.Equals(backend.CurrentName, previous, StringComparison.Ordinal))
                    backend.Activate(previous);
                backend.RestoreFile(name, null);
                backend.RefreshList();
            }
            catch (Exception restoreError) { logError?.Invoke(restoreError); }
            return CommandResult.Failure(CommandResultCodes.SaveFailed, $"使用新主界面文件失败，已撤销：{ex.Message}");
        }
        return Success(request.Activate ? $"已新建并使用主界面文件“{name}”" : $"已新建主界面文件“{name}”，未切换当前主界面");
    }

    internal static CommandResult Read(IMainWindowLayoutBackend backend, Action<Exception>? logError = null)
    {
        MainWindowLayoutSnapshot snapshot;
        try
        {
            snapshot = new MainWindowLayoutSnapshot
            {
                Name = backend.CurrentName,
                LayoutJson = backend.ReadCurrentJson(),
                Layouts = backend.ListNames().ToList(),
                IsManagementMode = backend.IsManagementMode,
            };
        }
        catch (Exception ex)
        {
            logError?.Invoke(ex);
            return CommandResult.Failure(CommandResultCodes.InternalError, $"无法读取主界面文件：{ex.Message}");
        }
        if (System.Text.Encoding.UTF8.GetByteCount(snapshot.LayoutJson) > MainWindowLayoutRules.MaxUtf8Bytes)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "主界面文件超过 2 MB，无法获取");
        return new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Ok,
            Message = $"已读取主界面文件“{snapshot.Name}”",
            Data = JsonSerializer.Serialize(snapshot, SnapshotJsonOptions),
        };
    }

    private static CommandResult Success(string message) => new()
    {
        Success = true,
        Code = CommandResultCodes.Ok,
        Message = message,
    };
}
