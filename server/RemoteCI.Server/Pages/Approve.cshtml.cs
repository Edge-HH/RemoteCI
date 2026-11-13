using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;
using RemoteCI.Server.Services;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 聊天机器人管理高危操作的一次性网页授权页（匿名访问，凭一次性令牌）。GET 只展示操作摘要，
/// 避免聊天软件预览链接时误执行；输入账号密码或一次性验证码后 POST 才执行，令牌随即作废。
/// 令牌本身是只有收件人知道的秘密，且执行还需密码/验证码，跨站请求无法伪造，
/// 因此不要求防伪 Cookie（部分聊天软件的内置浏览器会丢弃 Cookie，与 /Act 一致）。
/// </summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class ApproveModel(OperationApprovalService approvals) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Token { get; set; } = string.Empty;
    [BindProperty] public string? Password { get; set; }
    [BindProperty] public string? Code { get; set; }
    [BindProperty] public bool UseRecoveryCode { get; set; }

    public OperationApprovalState State { get; private set; } = new(null, null);
    public string? Result { get; private set; }
    public string? Error { get; private set; }
    public string ActionLabel => State.Token is { } token ? OperationApprovalActions.Label(token.Action) : "管理操作";
    public bool IsDestructive => State.Token?.Action is OperationApprovalActions.DeleteUser or OperationApprovalActions.DeleteClass;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        NoStore();
        State = await approvals.InspectAsync(Token, ct);
        return Page();
    }

    [EnableRateLimiting("auth")]
    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        NoStore();
        try
        {
            Result = await approvals.ExecuteAsync(Token, Password, Code, UseRecoveryCode, ct);
            State = await approvals.InspectAsync(Token, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or IdentityOperationException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            State = await approvals.InspectAsync(Token, ct);
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