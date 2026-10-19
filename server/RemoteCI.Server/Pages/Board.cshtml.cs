using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteCI.Server.Services;

namespace RemoteCI.Server.Pages;

/// <summary>电子班牌（匿名，凭私密链接）：本班当前课程、下一节与今日课表，每 15 秒刷新。</summary>
public sealed class BoardModel(ClassBoardService boards) : PageModel
{
    [BindProperty(SupportsGet = true)] public string Token { get; set; } = string.Empty;
    public BoardData Data { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await LoadAsync(ct) is { } missing) return missing;
        return Page();
    }

    /// <summary>定时刷新只替换内容区，时钟由页面脚本按服务端时间推算。</summary>
    public async Task<IActionResult> OnGetContentAsync(CancellationToken ct)
    {
        if (await LoadAsync(ct) is { } missing) return missing;
        return Partial("_BoardContent", Data);
    }

    private async Task<IActionResult?> LoadAsync(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
        if (await boards.ResolveAsync(Token, ct) is not { } board) return NotFound();
        Data = await boards.BuildAsync(board, ct);
        return null;
    }
}
