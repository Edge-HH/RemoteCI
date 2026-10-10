using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

/// <summary>
/// 首次部署的初始化向导：第一步在服务端还没有任何账号时创建系统管理员（不再从日志读取初始密码），
/// 第二步由系统管理员手动新建第一个班级（不再自动创建默认班级）。两步都完成后转到总览。
/// </summary>
public sealed class SetupModel(
    UserManager<AppUser> users,
    SignInManager<AppUser> signIn,
    IdentityCoordinator identities,
    ClassroomService classrooms,
    AuthorizationSyncService authorizationSync) : PageModel
{
    public enum SetupStep
    {
        SystemAdmin,
        FirstClass,
    }

    public SetupStep Step { get; private set; }

    [BindProperty]
    public AdminInput Admin { get; set; } = new();

    [BindProperty]
    [Required(ErrorMessage = "请填写班级名称")]
    [StringLength(40, ErrorMessage = "班级名称最多 40 个字符")]
    public string ClassName { get; set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        var status = await identities.GetSetupStatusAsync(ct);
        if (status.NeedsSystemAdmin)
        {
            Step = SetupStep.SystemAdmin;
            return Page();
        }
        if (await CurrentAdminAsync() is null)
            return RedirectToPage("/Login");
        if (!status.NeedsFirstClass)
            return RedirectToPage("/Index");
        Step = SetupStep.FirstClass;
        return Page();
    }

    public async Task<IActionResult> OnPostAdminAsync(CancellationToken ct)
    {
        Step = SetupStep.SystemAdmin;
        if (!(await identities.GetSetupStatusAsync(ct)).NeedsSystemAdmin)
        {
            TempData["Error"] = "系统管理员已创建，请直接登录。";
            return SeeOther("/Login");
        }
        ModelState.Remove(nameof(ClassName));
        if (!string.Equals(Admin.Password, Admin.ConfirmPassword, StringComparison.Ordinal))
            ModelState.AddModelError($"{nameof(Admin)}.{nameof(AdminInput.ConfirmPassword)}", "两次输入的密码不一致");
        if (!ModelState.IsValid) return Page();
        try
        {
            var created = await identities.CreateSystemOwnerAsync(new SetupSystemAdminRequest
            {
                Username = Admin.Username,
                DisplayName = Admin.DisplayName ?? string.Empty,
                Password = Admin.Password,
            }, ct);
            await authorizationSync.SyncAsync(ct);
            var owner = await users.FindByIdAsync(created.Id.ToString());
            if (owner is not null) await signIn.SignInAsync(owner, isPersistent: false);
        }
        catch (IdentityOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
        TempData["Message"] = "系统管理员已创建，接下来新建第一个班级。";
        return SeeOther("/Setup");
    }

    public async Task<IActionResult> OnPostClassAsync(CancellationToken ct)
    {
        Step = SetupStep.FirstClass;
        if (await CurrentAdminAsync() is null) return RedirectToPage("/Login");
        if (!(await identities.GetSetupStatusAsync(ct)).NeedsFirstClass) return SeeOther("/Index");
        // 这一步只提交班级名称；管理员表单没有提交时模型绑定会回退到空前缀，因此只保留 ClassName 的校验结果。
        foreach (var key in ModelState.Keys.Where(key => key != nameof(ClassName)).ToList())
            ModelState.Remove(key);
        if (!ModelState.IsValid) return Page();
        try
        {
            var created = await classrooms.CreateAsync(ClassName.Trim(), ct);
            await authorizationSync.SyncAsync(ct);
            Response.Cookies.Append(WebPageModel.CurrentClassCookie, created.Id.ToString(), new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                IsEssential = true,
                MaxAge = TimeSpan.FromDays(30),
            });
        }
        catch (IdentityOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            return Page();
        }
        TempData["Message"] = "第一个班级已创建。点击“生成配对码”把教室端插件连接到这个班级，或到“班级管理”继续添加班级。";
        return SeeOther("/Index");
    }

    private async Task<AppUser?> CurrentAdminAsync() =>
        User.Identity?.IsAuthenticated == true && await users.GetUserAsync(User) is { Enabled: true, Role: UserRole.Admin } user
            ? user
            : null;

    /// <summary>表单 POST 之后用 303 让浏览器以 GET 打开下一页，避免返回时重复提交。</summary>
    private IActionResult SeeOther(string path)
    {
        Response.StatusCode = StatusCodes.Status303SeeOther;
        Response.Headers.Location = path;
        return new EmptyResult();
    }

    public sealed class AdminInput
    {
        [Required(ErrorMessage = "请填写登录 ID")]
        [RegularExpression("^[A-Za-z0-9._-]{3,32}$", ErrorMessage = "ID 需为 3-32 位字母、数字、点、下划线或短横线")]
        public string Username { get; set; } = "admin";

        [StringLength(40, ErrorMessage = "显示名称最多 40 个字符")]
        public string? DisplayName { get; set; } = "系统管理员";

        [Required(ErrorMessage = "请设置密码")]
        [StringLength(128, MinimumLength = 8, ErrorMessage = "密码需为 8-128 个字符")]
        public string Password { get; set; } = string.Empty;

        [Required(ErrorMessage = "请再次输入密码")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
