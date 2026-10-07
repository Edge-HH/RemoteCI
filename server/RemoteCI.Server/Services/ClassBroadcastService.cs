using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 集控广播：向多个班级（可按分组展开）同时发送同一条命令（通知/清除通知/电源/语音消息）。
/// 每个班级独立按发送者的班内有效权限鉴权、独立路由到该班插件，单班失败不影响其余班级。
/// </summary>
public sealed class ClassBroadcastService(
    IdentityCoordinator identities,
    ClassroomService classrooms,
    ClassAccessService access,
    PeerRegistry peers)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>支持广播的命令白名单：单发路径已覆盖逐班操作，广播只开放有集体意义的命令。</summary>
    private static bool IsBroadcastable(CommandKind command) => command is
        CommandKind.SendNotification or
        CommandKind.ClearNotifications or
        CommandKind.Power or
        CommandKind.SendVoiceMessage or
        CommandKind.TeacherComing;

    public async Task<BatchClassOperationResult> BroadcastAsync(
        AuthPrincipal sender, BroadcastCommandRequest request, CancellationToken ct = default)
    {
        var required = CommandPermissions.Required(request.Command);
        if (required == UserPermissions.None || !IsBroadcastable(request.Command))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "该命令不支持批量广播");

        var classIds = await classrooms.ResolveTargetClassIdsAsync(request.ClassIds, request.GroupIds, ct);
        var forceSender = request.Command == CommandKind.SendNotification
            ? await identities.GetForceSenderInTitleAsync(ct)
            : false;
        var classNameById = (await classrooms.ListAsync(ct)).ToDictionary(x => x.Id, x => x.Name);

        var tasks = classIds.Select(classId =>
            SendToOneAsync(sender, request, required, classId, forceSender, classNameById, ct));
        var results = await Task.WhenAll(tasks);
        return new BatchClassOperationResult { Results = [.. results] };
    }

    private async Task<BatchClassItemResult> SendToOneAsync(
        AuthPrincipal sender,
        BroadcastCommandRequest request,
        UserPermissions required,
        Guid classId,
        bool forceSender,
        IReadOnlyDictionary<Guid, string> classNameById,
        CancellationToken ct)
    {
        var user = sender.User!;
        try
        {
            var classPermissions = await access.GetEffectivePermissionsAsync(
                user.Id, user.Role, classId, user.GrantedPermissions, ct);
            if (!classPermissions.HasFlag(required))
                return Fail(classId, "没有该班级的操作权限");

            var result = await peers.SendCommandAndWaitAsync(new CommandMessage
            {
                Command = request.Command,
                ClassId = classId,
                RequestedBy = sender.User.WithPermissions(classPermissions),
                Notification = request.Notification is { } notification
                    ? new NotificationRequest
                    {
                        Title = notification.Title,
                        Message = notification.Message,
                        ForceSenderInTitle = forceSender,
                        IsNotificationEffectEnabled = notification.IsNotificationEffectEnabled,
                        IsNotificationSoundEnabled = notification.IsNotificationSoundEnabled,
                        IsSpeechEnabled = notification.IsSpeechEnabled,
                        IsNotificationTopmostEnabled = notification.IsNotificationTopmostEnabled,
                        DurationSeconds = notification.DurationSeconds,
                        RepeatCounts = notification.RepeatCounts,
                        IsRollingEnabled = notification.IsRollingEnabled,
                    }
                    : null,
                PowerAction = request.PowerAction,
                VoiceMessage = request.VoiceMessage,
            }, classId, CommandTimeout, ct);

            return new BatchClassItemResult
            {
                ClassId = classId,
                Success = result.Success,
                Message = result.Success ? null : result.Message,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(classId, ex.Message);
        }

        BatchClassItemResult Fail(Guid id, string message) => new()
        {
            ClassId = id,
            Success = false,
            Message = $"{classNameById.GetValueOrDefault(id, id.ToString())}：{message}",
        };
    }
}
