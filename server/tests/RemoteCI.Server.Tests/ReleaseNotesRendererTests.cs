using RemoteCI.Server.Services;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class ReleaseNotesRendererTests
{
    [Fact]
    public void ToHtml_RendersGitHubMarkdown()
    {
        var html = ReleaseNotesRenderer.ToHtml("## 新功能\n\n- **调休**设置\n- [文档](https://example.com)");

        Assert.Contains("<h2", html);
        Assert.Contains("<li><strong>调休</strong>设置</li>", html);
        Assert.Contains("href=\"https://example.com\"", html);
        Assert.Contains("target=\"_blank\"", html);
    }

    [Fact]
    public void ToHtml_EscapesRawHtmlAndDropsUnsafeLinks()
    {
        var html = ReleaseNotesRenderer.ToHtml("<script>alert(1)</script>\n\n[x](javascript:alert(1))");

        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("javascript:", html);
    }

    [Fact]
    public void ToHtml_EmptyBodyRendersNothing() => Assert.Equal("", ReleaseNotesRenderer.ToHtml("  "));
}
