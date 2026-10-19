using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;

namespace RemoteCI.Server.Pages;

/// <summary>当前班级的电子班牌链接：新建（链接和二维码只显示一次）与撤销。系统管理员或本班班主任可用。</summary>
[Authorize]
public sealed class ClassBoardsModel(UserManager<AppUser> users, ClassBoardService boards) : WebPageModel(users)
{
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public bool ShowTeachers { get; set; } = true;

    public IReadOnlyList<ClassBoardView> Boards { get; private set; } = [];
    public ClassBoardCreated? Created { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireManagerAsync(ct) is { } denied) return denied;
        Boards = await boards.ListAsync(CurrentUser, CurrentClassId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (await RequireManagerAsync(ct) is { } denied) return denied;
        try
        {
            // 直接在本次响应里展示链接与二维码：令牌不落库明文，刷新页面后无法再取回。
            Created = await boards.CreateAsync(CurrentUser, CurrentClassId, Name, ShowTeachers,
                $"{Request.Scheme}://{Request.Host}{Request.PathBase}", ct);
            Response.Headers.CacheControl = "no-store";
        }
        catch (ArgumentException ex) { TempData["Error"] = ex.Message; }
        Boards = await boards.ListAsync(CurrentUser, CurrentClassId, ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken ct)
    {
        if (await RequireManagerAsync(ct) is { } denied) return denied;
        try
        {
            await boards.RevokeAsync(CurrentUser, id, ct);
            TempData["Message"] = "班牌链接已撤销，使用该链接的屏幕会提示重新生成。";
        }
        catch (Exception ex) when (ex is KeyNotFoundException or UnauthorizedAccessException) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    private async Task<IActionResult?> RequireManagerAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        return CurrentClass is not null && await boards.CanManageAsync(CurrentUser, CurrentClassId, ct) ? null : RedirectToPage("/Denied");
    }
}
