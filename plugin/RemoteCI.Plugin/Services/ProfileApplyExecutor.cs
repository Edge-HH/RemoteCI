using RemoteCI.Shared.Models;
using RemoteCI.Shared;

namespace RemoteCI.Plugin.Services;

/// <summary>一个宿主档案事务的最小接口；实现负责把快照恢复到内存和持久化文件。</summary>
internal interface IProfileApplicationBackend
{
    string ReadCurrentJson();
    bool ProfileExists(string name);
    object CaptureState();
    void ApplyCurrentJson(string json);
    void CreateAndActivate(string json, string name);
    void Save();
    void RestoreState(object snapshot);
}

/// <summary>与 UI 和 ClassIsland 类型无关的事务编排：验证完整候选后才写入，失败回滚。</summary>
internal static class ProfileApplyExecutor
{
    internal static CommandResult Apply(ProfileApplyRequest? request, IProfileApplicationBackend backend,
        Action<Exception>? logError = null, DateTime? today = null)
    {
        if (request is null) return CommandResult.Failure(CommandResultCodes.InvalidRequest, "缺少档案应用参数");
        string candidate;
        string? name = null;
        ProfileDocument.TempLayerApplyResult? layers = null;
        try
        {
            if (request.Mode == ProfileApplyMode.TempLayers)
            {
                layers = ProfileDocument.ApplyTempLayers(backend.ReadCurrentJson(), request, today ?? DateTime.Today);
                candidate = layers.Json;
            }
            else candidate = ProfileDocument.Apply(backend.ReadCurrentJson(), request);
            if (request.Mode == ProfileApplyMode.CreateAndActivate)
            {
                name = NormalizeImportName(request.ImportProfileName);
                if (backend.ProfileExists(name))
                    return CommandResult.Failure(CommandResultCodes.InvalidRequest, $"档案“{name}”已存在，请更换名称");
            }
        }
        catch (ArgumentException ex) { return CommandResult.Failure(CommandResultCodes.InvalidRequest, ex.Message); }
        catch (Exception ex)
        {
            logError?.Invoke(ex);
            return CommandResult.Failure(CommandResultCodes.SaveFailed, $"无法读取当前档案：{ex.Message}");
        }

        object snapshot;
        try { snapshot = backend.CaptureState(); }
        catch (Exception ex)
        {
            logError?.Invoke(ex);
            return CommandResult.Failure(CommandResultCodes.SaveFailed, $"无法备份当前档案：{ex.Message}");
        }
        try
        {
            if (request.Mode == ProfileApplyMode.CreateAndActivate) backend.CreateAndActivate(candidate, name!);
            else backend.ApplyCurrentJson(candidate);
            backend.Save();
        }
        catch (Exception ex)
        {
            logError?.Invoke(ex);
            try { backend.RestoreState(snapshot); }
            catch (Exception rollbackError)
            {
                logError?.Invoke(rollbackError);
                return CommandResult.Failure(CommandResultCodes.SaveFailed,
                    $"保存档案失败，恢复旧档案也失败，请检查磁盘权限：{rollbackError.Message}");
            }
            return CommandResult.Failure(CommandResultCodes.SaveFailed, $"保存档案失败，已恢复原档案：{ex.Message}");
        }
        return new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Ok,
            Message = request.Mode switch
            {
                ProfileApplyMode.MergeCurrent => "已更新当前档案及必要依赖",
                ProfileApplyMode.ReplaceSections => "已整体替换选中的档案类别",
                ProfileApplyMode.TempLayers => $"已下发 {layers!.Applied} 个临时层" +
                    (layers.Skipped > 0 ? $"，跳过 {layers.Skipped} 个已过期的临时层" : string.Empty),
                _ => $"已创建并启用档案“{name}”",
            },
        };
    }

    internal static string NormalizeImportName(string? input)
    {
        return ProfileApplyRequest.NormalizeImportName(input);
    }
}
