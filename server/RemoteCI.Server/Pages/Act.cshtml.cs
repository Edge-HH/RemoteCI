using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 聊天卡片按钮打开的确认页（匿名访问，凭一次性令牌）。GET 只展示，避免聊天软件预览链接时误执行；
/// 点击确认后 POST 才执行，令牌随即作废。令牌本身就是只有收件人知道的秘密，跨站请求无法伪造它，
/// 因此不再要求防伪 Cookie（部分聊天软件的内置浏览器会丢弃 Cookie）。
/// </summary>
[IgnoreAntiforgeryToken]
public sealed class ActModel(ActionLinkService links) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Token { get; set; } = string.Empty;
    [BindProperty] public string? Note { get; set; }
    /// <summary>上次提交返回了需要确认的冲突（如任课冲突），勾选后带确认重新提交。</summary>
    [BindProperty] public bool AcknowledgeConflicts { get; set; }

    public IReadOnlyList<SwapConflict> Conflicts { get; private set; } = [];

    public ActionLinkState State { get; private set; } = new(null, null, null);
    public SwapRequestView? Result { get; private set; }
    public string? Error { get; private set; }
    public string ActionLabel => State.Token is { } token ? ActionLinkActions.Label(token.Action) : "操作";
    public bool AllowsNote => State.Token?.Action is ActionLinkActions.SwapApprove or ActionLinkActions.SwapReject;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        NoStore();
        State = await links.InspectAsync(Token, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        NoStore();
        try
        {
            Result = await links.ExecuteAsync(Token, Note, AcknowledgeConflicts, ct);
            State = await links.InspectAsync(Token, ct);
        }
        catch (SwapOperationException ex) when (ex.Conflicts is { Count: > 0 })
        {
            Error = "这次换课存在需要确认的冲突，请确认后再同意。";
            Conflicts = ex.Conflicts;
            State = await links.InspectAsync(Token, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or SwapOperationException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            State = await links.InspectAsync(Token, ct);
        }
        return Page();
    }

    private void NoStore()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    }
}
