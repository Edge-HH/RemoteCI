using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>识别课表拉取消息并归一化任务来源；实际互斥与收集由插件编排器处理。</summary>
internal sealed class SchedulePullRequestHandler(Func<ScheduleSyncRequest, ScheduleSyncStatus> requestScheduleSync)
{
    public bool TryHandle(Envelope envelope)
    {
        if (envelope.Type != Protocol.MessageTypeSchedulePull) return false;
        var request = ConvertPayload<ScheduleSyncRequest>(envelope.Payload)
            ?? ScheduleSyncRequest.Create(ScheduleSyncSource.Unknown, envelope.MessageId);
        if (string.IsNullOrWhiteSpace(request.TaskId)) request.TaskId = envelope.MessageId;
        requestScheduleSync(request);
        return true;
    }

    private static T? ConvertPayload<T>(object? payload) => payload is null ? default : JsonSerializer.Deserialize<T>(
        JsonSerializer.Serialize(payload), JsonDefaults.Options);
}
