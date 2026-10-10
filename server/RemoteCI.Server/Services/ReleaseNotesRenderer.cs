using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace RemoteCI.Server.Services;

/// <summary>
/// 把 GitHub release 正文（GFM）渲染为 WebUI 可直接输出的 HTML。
/// 正文来自远端，因此禁用原始 HTML，并只保留 http/https/mailto 链接与图片地址。
/// </summary>
public static class ReleaseNotesRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static string ToHtml(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var document = Markdown.Parse(markdown, Pipeline);
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (!IsSafeUrl(link.Url)) link.Url = "";
            if (!link.IsImage)
            {
                var attributes = link.GetAttributes();
                attributes.AddPropertyIfNotExist("target", "_blank");
                attributes.AddPropertyIfNotExist("rel", "noopener noreferrer");
            }
        }
        foreach (var link in document.Descendants<AutolinkInline>())
        {
            if (!IsSafeUrl(link.Url)) link.Url = "";
        }
        return document.ToHtml(Pipeline);
    }

    internal static bool IsSafeUrl(string? url) =>
        string.IsNullOrEmpty(url) ||
        url.StartsWith('#') ||
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeMailto);
}
