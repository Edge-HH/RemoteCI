using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class LanSessionLogicTests
{
    [Fact]
    public void ValidateAuthProofRequest_AcceptsFreshProofChallenge()
    {
        var challenge = new AuthChallenge
        {
            ChallengeId = "challenge",
            Nonce = "nonce",
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30),
        };

        Assert.Null(LanSessionLogic.ValidateAuthProofRequest(
            new Envelope { Type = Protocol.MessageTypeAuthProof }, challenge, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ValidateAuthProofRequest_RejectsMissingChallengeWrongTypeAndExpiry()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.NotNull(LanSessionLogic.ValidateAuthProofRequest(
            new Envelope { Type = Protocol.MessageTypeAuthProof }, null, now));
        Assert.NotNull(LanSessionLogic.ValidateAuthProofRequest(
            new Envelope { Type = Protocol.MessageTypeCommand }, new AuthChallenge { ExpiresAt = now.AddSeconds(30) }, now));
        Assert.NotNull(LanSessionLogic.ValidateAuthProofRequest(
            new Envelope { Type = Protocol.MessageTypeAuthProof },
            new AuthChallenge { ExpiresAt = now.AddSeconds(-1) }, now));
    }

    [Theory]
    [InlineData(true, UserPermissions.All, UserPermissions.None)]            // 镜像过期:扩展命令(静态权限 None)也必须拒绝。
    [InlineData(true, UserPermissions.All, UserPermissions.SendNotifications)]
    [InlineData(false, UserPermissions.ViewCurrentCourse, UserPermissions.SendNotifications)]
    [InlineData(false, UserPermissions.ViewCurrentCourse, UserPermissions.ManageSchedule)]
    public void CommandDenied_RefusesExpiredMirrorOrInsufficientPermission(
        bool mirrorExpired, UserPermissions permissions, UserPermissions required)
    {
        Assert.True(LanSessionLogic.CommandDenied(mirrorExpired, permissions, required));
        Assert.Equal(
            mirrorExpired ? "授权镜像超过 24 小时，仅允许查看课程" : "权限不足",
            LanSessionLogic.CommandDeniedMessage(mirrorExpired));
    }

    [Theory]
    [InlineData(false, UserPermissions.All, UserPermissions.SendNotifications)]
    [InlineData(false, UserPermissions.All, UserPermissions.ManageSchedule)]
    [InlineData(false, UserPermissions.All, UserPermissions.SystemControl)]
    public void CommandDenied_AllowsAuthorizedCommandsWithFreshMirror(
        bool mirrorExpired, UserPermissions permissions, UserPermissions required)
    {
        Assert.False(LanSessionLogic.CommandDenied(mirrorExpired, permissions, required));
    }
}
