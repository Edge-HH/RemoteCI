using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>服务端“从设备收集”的读取逻辑：只读取宿主当前档案，不写入任何内容。</summary>
internal static class ProfileCollectExecutor
{
    /// <summary>读取当前档案供服务端收集；超过档案大小上限时拒绝，避免服务端拿到无法保存的内容。</summary>
    internal static CommandResult Read(IProfileApplicationBackend backend, Action<Exception>? logError = null)
    {
        string json;
        try { json = backend.ReadCurrentJson(); }
        catch (Exception ex)
        {
            logError?.Invoke(ex);
            return CommandResult.Failure(CommandResultCodes.InternalError, $"无法读取当前档案：{ex.Message}");
        }
        // 与上传、保存同一上限；超过时服务端无法保存，直接说明原因而不是返回截断内容。
        if (System.Text.Encoding.UTF8.GetByteCount(json) > ProfileDocument.MaxUtf8Bytes)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "设备档案超过 5 MB，无法收集");
        return new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "已读取设备档案", Data = json };
    }
}
