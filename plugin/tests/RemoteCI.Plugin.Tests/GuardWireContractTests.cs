using System.Text.Json;
using RemoteCI.Guard;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

/// <summary>
/// 守护用 DataContractJsonSerializer 收发 WebSocket 消息，服务端用 System.Text.Json：
/// 两边格式不一致时消息会被静默丢弃，守护看似已连接却声明不了能力、收不到命令。
/// </summary>
public sealed class GuardWireContractTests
{
    [Fact]
    public void GuardCapabilities_AreReadableByServer()
    {
        var json = GuardJson.Serialize(new GuardEnvelope<GuardPeerCapabilities>
        {
            Type = Protocol.MessageTypePeerCapabilities,
            Payload = new GuardPeerCapabilities
            {
                SoftwareVersion = "3.3.1.8",
                Capabilities = [RemoteCiCapabilities.AppGuard, RemoteCiCapabilities.AppGuardLaunch, RemoteCiCapabilities.AppGuardManage],
            },
        });

        var envelope = JsonSerializer.Deserialize<Envelope>(json, JsonDefaults.Options)!;
        var capabilities = ConvertPayload<PeerCapabilities>(envelope.Payload)!;

        Assert.Equal(Protocol.Version, envelope.ProtocolVersion);
        Assert.Equal(Protocol.MessageTypePeerCapabilities, envelope.Type);
        Assert.Equal("3.3.1.8", capabilities.SoftwareVersion);
        Assert.Contains(RemoteCiCapabilities.AppGuardManage, capabilities.Capabilities);
    }

    [Fact]
    public void GuardCommandResultAndEvents_AreReadableByServer()
    {
        var result = JsonSerializer.Deserialize<Envelope>(GuardJson.Serialize(new GuardEnvelope<GuardCommandResult>
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = "abc",
            Payload = GuardCommandResult.Ok("应用守护已关闭"),
        }), JsonDefaults.Options)!;
        var events = JsonSerializer.Deserialize<Envelope>(GuardJson.Serialize(new GuardEnvelope<GuardEventBatch>
        {
            Type = Protocol.MessageTypeAppGuardEvent,
            Payload = new GuardEventBatch { Events = [new GuardEventLine { Kind = GuardEventKinds.Restored, AtUnixMs = 42, Detail = "x" }] },
        }), JsonDefaults.Options)!;

        Assert.Equal("abc", result.ReplyToMessageId);
        Assert.True(ConvertPayload<CommandResult>(result.Payload)!.Success);
        var batch = ConvertPayload<AppGuardEventBatch>(events.Payload)!;
        Assert.Equal(GuardEventKinds.Restored, Assert.Single(batch.Events).Kind);
        Assert.Equal(42, batch.Events[0].AtUnixMs);
    }

    [Fact]
    public void ServerCommandAndPolicy_AreReadableByGuard()
    {
        var command = JsonSerializer.Serialize(Envelope.Command(new CommandMessage
        {
            Command = CommandKind.StopAppGuard,
            RequestedBy = new UserProfile { Role = UserRole.Admin, Permissions = UserPermissions.ManageUsers },
        }), JsonDefaults.Options);
        var policy = JsonSerializer.Serialize(Envelope.AppGuardPolicy(new AppGuardPolicy
        {
            Enabled = true,
            RestartAfterExitDelayMinutes = 40,
            BackupPaths = [@"D:\Backup"],
            OrganizationName = "实验中学",
        }), JsonDefaults.Options);

        // 守护先按通用载荷读出类型，再按具体类型解析。
        Assert.Equal("command", GuardJson.Deserialize<GuardEnvelope<Dictionary<string, object>>>(command)?.Type);
        var guardCommand = GuardJson.Deserialize<GuardEnvelope<GuardCommand>>(command)!;
        Assert.Equal(Protocol.Version, guardCommand.ProtocolVersion);
        Assert.Equal(GuardCommands.StopAppGuard, guardCommand.Payload!.Command);
        Assert.Equal((int)UserRole.Admin, guardCommand.Payload.RequestedBy!.Role);
        Assert.Equal((int)UserPermissions.ManageUsers, guardCommand.Payload.RequestedBy.Permissions);
        var guardPolicy = GuardJson.Deserialize<GuardEnvelope<GuardPolicyPayload>>(policy)!.Payload!;
        Assert.True(guardPolicy.Enabled);
        Assert.Equal(40, guardPolicy.RestartAfterExitDelayMinutes);
        Assert.Equal([@"D:\Backup"], guardPolicy.BackupPaths);
        Assert.Equal("实验中学", guardPolicy.OrganizationName);
    }

    private static T? ConvertPayload<T>(object? payload) =>
        payload is JsonElement element ? element.Deserialize<T>(JsonDefaults.Options) : default;
}
