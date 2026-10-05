using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>换课申请向教室端下发换课命令的接缝；生产走 PeerRegistry，测试可替换为假插件。</summary>
public interface IScheduleCommandSender
{
    Task<CommandResult> SendAsync(Guid classId, CommandMessage command, TimeSpan timeout, CancellationToken ct);
}

public sealed class PeerScheduleCommandSender(PeerRegistry peers) : IScheduleCommandSender
{
    public Task<CommandResult> SendAsync(Guid classId, CommandMessage command, TimeSpan timeout, CancellationToken ct) =>
        peers.SendCommandAndWaitAsync(command, classId, timeout, ct);
}
