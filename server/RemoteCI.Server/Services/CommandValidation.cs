using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

public sealed record CommandValidationError(string Code, string Message);

/// <summary>命令参数校验规则，WebSocket 与 REST 通用命令入口共用，保证两条路径行为一致。</summary>
public static class CommandValidation
{
    public static CommandValidationError? ValidateParameters(CommandMessage command)
    {
        if (command.Command == CommandKind.SendVoiceMessage &&
            !VoiceMessageRequest.TryDecode(command.VoiceMessage, out _))
            return new CommandValidationError(CommandResultCodes.InvalidRequest, "语音格式无效或超过 60 秒");
        if (PeerRegistry.GetNotificationError(command) is { } notificationError)
            return new CommandValidationError(CommandResultCodes.InvalidRequest, notificationError);
        if (command.Command is CommandKind.InstallPlugins or CommandKind.UninstallPlugins or CommandKind.SetPluginEnabled)
            return ValidatePluginManagement(command);
        return ValidateOptionalCommandPayload(command);
    }

    private static CommandValidationError? ValidatePluginManagement(CommandMessage command)
    {
        if (command.PluginManagement is not { PluginIds.Count: > 0 } pluginRequest)
            return new CommandValidationError(CommandResultCodes.InvalidRequest, "缺少插件管理参数");
        var validAction = command.Command switch
        {
            CommandKind.InstallPlugins => pluginRequest.Action == PluginActionKind.Install,
            CommandKind.UninstallPlugins => pluginRequest.Action == PluginActionKind.Uninstall,
            CommandKind.SetPluginEnabled => pluginRequest.Action is PluginActionKind.Enable or PluginActionKind.Disable,
            _ => false,
        };
        return validAction ? null : new CommandValidationError(
            CommandResultCodes.InvalidRequest, "插件操作与命令类型不匹配");
    }

    private static CommandValidationError? ValidateOptionalCommandPayload(CommandMessage command)
    {
        if (command.Command == CommandKind.SetPluginManagementPolicy && command.PluginManagementPolicy is null)
            return new CommandValidationError(CommandResultCodes.InvalidRequest, "缺少插件管理策略");
        if (command.Command == CommandKind.UpdateTimeLayout &&
            (command.TimeLayoutUpdate is null || command.TimeLayoutUpdate.Points.Count == 0))
            return new CommandValidationError(CommandResultCodes.InvalidRequest, "缺少时间表参数");
        if (command.Command == CommandKind.DistributeProfile &&
            (command.ProfileDistribution is null ||
             command.ProfileDistribution.Sections == ProfileDistributionSection.None ||
             string.IsNullOrWhiteSpace(command.ProfileDistribution.ProfileJson)))
            return new CommandValidationError(CommandResultCodes.InvalidRequest, "缺少档案分发参数");
        if (command.Command == CommandKind.SetSubjectTeacher)
        {
            if (command.SubjectTeacher is null || command.SubjectTeacher.SubjectId == Guid.Empty)
                return new CommandValidationError(CommandResultCodes.InvalidRequest, "缺少科目教师参数");
            if (command.SubjectTeacher.TeacherName is { Length: > SubjectTeacherRequest.MaxTeacherNameLength })
                return new CommandValidationError(CommandResultCodes.InvalidRequest, "教师名过长");
        }
        if (command.Command == CommandKind.SetSubjectLocation)
        {
            if (command.SubjectLocation is null || command.SubjectLocation.SubjectId == Guid.Empty)
                return new CommandValidationError(CommandResultCodes.InvalidRequest, "缺少科目地点参数");
            if (command.SubjectLocation.Location is { Length: > SubjectLocations.MaxLength })
                return new CommandValidationError(CommandResultCodes.InvalidRequest, "科目地点过长");
        }
        if (command.Command == CommandKind.ExecuteTerminalCommand)
        {
            var terminal = command.TerminalCommand;
            if (terminal is null || string.IsNullOrWhiteSpace(terminal.Command))
                return new CommandValidationError(CommandResultCodes.InvalidRequest, "缺少终端命令");
            if (terminal.Command.Trim().Length > TerminalCommandRequest.MaxCommandLength)
                return new CommandValidationError(CommandResultCodes.InvalidRequest, "终端命令过长");
            if (terminal.WorkingDirectory is { Length: > TerminalCommandRequest.MaxWorkingDirectoryLength })
                return new CommandValidationError(CommandResultCodes.InvalidRequest, "工作目录路径过长");
            if (terminal.TimeoutSeconds is < 1 or > TerminalCommandRequest.MaxTimeoutSeconds)
                return new CommandValidationError(CommandResultCodes.InvalidRequest, "执行超时必须在 1-10 秒之间");
        }
        // 中转方式只能经 POST /api/files/{id}/send 下发，由服务端登记目标设备与下载凭据。
        if (command.Command == CommandKind.SendFile && command.FileDistribution?.IsTransfer == true)
            return new CommandValidationError(CommandResultCodes.InvalidRequest, "大文件请先上传到 POST /api/files，再用 POST /api/files/{id}/send 发送");
        if (command.Command == CommandKind.SendFile && !FileDistributionRequest.TryDecode(command.FileDistribution, out _))
            return new CommandValidationError(CommandResultCodes.InvalidRequest, "文件分发参数无效，文件不能为空且不能超过 10 MB");
        return command.Command == CommandKind.JoinManagement && command.ManagementJoin is null
            ? new CommandValidationError(CommandResultCodes.InvalidRequest, "缺少加入集控参数")
            : null;
    }
}