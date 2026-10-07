using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;

namespace RemoteCI.Server.Pages;

/// <summary>
/// WebUI 的错误状态页：由 UseStatusCodePagesWithReExecute 在空响应的 4xx/5xx 上重新执行到此，
/// 让非法地址落在带“返回主页”入口的页面上，而不是浏览器自带的错误页。
/// </summary>
[IgnoreAntiforgeryToken]
public sealed class StatusCodeModel(UserManager<AppUser> users) : PageModel
{
    public int Code { get; private set; }
    public string Title { get; private set; } = string.Empty;
    public string Message { get; private set; } = string.Empty;
    public string HomeUrl { get; private set; } = "/Login";
    public string HomeLabel { get; private set; } = "返回登录";

    public async Task OnGetAsync(int code)
    {
        // 直接访问 /status/xxx 时没有原始状态码，按请求的码回写，保证状态与页面内容一致。
        Code = code is >= 400 and <= 599 ? code : StatusCodes.Status404NotFound;
        if (HttpContext.Features.Get<IStatusCodeReExecuteFeature>() is null)
            Response.StatusCode = Code;
        (Title, Message) = Code switch
        {
            StatusCodes.Status404NotFound => ("页面不存在", "你访问的地址不存在，可能已被移动或输入有误。"),
            StatusCodes.Status403Forbidden => ("没有此项权限", "如果权限刚调整过，请刷新页面。"),
            StatusCodes.Status405MethodNotAllowed => ("请求方式不受支持", "请从页面内的入口重新操作。"),
            >= 500 => ("服务器出错了", "服务器暂时无法处理这个请求，请稍后重试。"),
            _ => ("请求无法完成", "请检查地址后重试。"),
        };
        await ResolveHomeAsync();
    }

    // 重新执行沿用原请求方法：表单 POST 出错时同样要渲染状态页，且不能再因防伪校验失败变回空响应。
    public Task OnPostAsync(int code) => OnGetAsync(code);

    // 与侧边栏品牌链接一致：有 WebUI 权限去概览，否则去个人账号；未登录去登录页。
    private async Task ResolveHomeAsync()
    {
        if (User.Identity?.IsAuthenticated != true) return;
        var id = users.GetUserId(User);
        var user = id is not null && Guid.TryParse(id, out var userId)
            ? await users.Users.AsNoTracking().Include(x => x.RoleDefinition).SingleOrDefaultAsync(x => x.Id == userId)
            : null;
        if (user is null || !user.Enabled) return;
        var permissions = RolePermissions.Effective(user.Role, user.GrantedPermissions, user.RoleDefinition.DefaultPermissions);
        (HomeUrl, HomeLabel) = permissions.HasFlag(UserPermissions.AccessWebUi)
            ? (Url.Page("/Index") ?? "/", "返回主页")
            : (Url.Page("/Account") ?? "/Account", "返回个人账号");
    }
}
