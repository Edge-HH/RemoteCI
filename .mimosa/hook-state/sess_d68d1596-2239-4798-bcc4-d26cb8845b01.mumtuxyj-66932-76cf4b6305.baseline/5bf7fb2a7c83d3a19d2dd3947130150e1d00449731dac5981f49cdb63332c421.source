using RemoteCI.Plugin.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class NotificationTitleTests
{
    [Fact]
    public void SenderNameUsesUsernameInsteadOfLoginId()
    {
        var sender = CommandHandler.GetNotificationSenderName(new UserProfile
        {
            Username = "student.id",
            DisplayName = "学生用户名",
        });

        Assert.Equal("学生用户名", sender);
    }

    [Fact]
    public void TitleAlwaysContainsAuthenticatedUsernamePrefix()
    {
        var title = CommandHandler.BuildNotificationTitle("student01", "  临时调课  ");

        Assert.Equal("由student01发送：临时调课", title);
    }

    [Fact]
    public void EmptyTitleStillContainsAuthenticatedUsernamePrefix()
    {
        var title = CommandHandler.BuildNotificationTitle("admin", "  ");

        Assert.Equal("由admin发送：RemoteCI 通知", title);
    }

    [Fact]
    public void DisabledForceSenderKeepsTitleWithoutPrefix()
    {
        var title = CommandHandler.BuildNotificationTitle("student01", "  临时调课  ", forceSenderInTitle: false);

        Assert.Equal("临时调课", title);
    }

    [Fact]
    public void DisabledForceSenderStillFallsBackForEmptyTitle()
    {
        var title = CommandHandler.BuildNotificationTitle("admin", "  ", forceSenderInTitle: false);

        Assert.Equal("RemoteCI 通知", title);
    }

    [Fact]
    public void NormalizeTitleFallsBackToDefaultForBlank()
    {
        Assert.Equal("RemoteCI 通知", CommandHandler.NormalizeNotificationTitle(null));
        Assert.Equal("RemoteCI 通知", CommandHandler.NormalizeNotificationTitle("   "));
    }

    [Fact]
    public void NormalizeTitleTrimsAndTruncatesToSixty()
    {
        Assert.Equal("临时调课", CommandHandler.NormalizeNotificationTitle("  临时调课  "));
        Assert.Equal(new string('长', 60), CommandHandler.NormalizeNotificationTitle(new string('长', 80)));
    }

    [Fact]
    public void NormalizeMessageKeepsBlankBodyEmpty()
    {
        Assert.Equal(string.Empty, CommandHandler.NormalizeNotificationMessage(null));
        Assert.Equal(string.Empty, CommandHandler.NormalizeNotificationMessage("   "));
        Assert.Equal("正文", CommandHandler.NormalizeNotificationMessage("  正文  "));
    }
}
