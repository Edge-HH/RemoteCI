using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class UsersModel(UserManager<AppUser> users, IdentityCoordinator identities, AccountRoleService roleService, AuthorizationSyncService authorizationSync)
    : WebPageModel(users)
{
    [BindProperty]
    public UserInput Create { get; set; } = new();
    [BindProperty]
    public UserInput Edit { get; set; } = new();
    public IReadOnlyList<UserListItem> Accounts { get; private set; } = [];
    public IReadOnlyList<AccountRoleInfo> RoleDefinitions { get; private set; } = [];
    [BindProperty] public RoleInput RoleEdit { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        Create.Role = Create.RoleId == AccountRole.AdministratorId ? UserRole.Admin : UserRole.User;
        if (Create.Role == UserRole.Admin && CurrentUser.Role != UserRole.Admin)
            return await CreateFailureAsync("仅管理员可创建管理员账号。", Array.Empty<string>(), ct);
        KeepModelStateEntries(nameof(Create));
        if (!ModelState.IsValid)
        {
            var invalidFields = InvalidCreateFields();
            var message = ModelState.Values.SelectMany(value => value.Errors)
                .Select(error => error.ErrorMessage)
                .FirstOrDefault(message => !string.IsNullOrWhiteSpace(message))
                ?? "请检查新建账号表单。";
            return await CreateFailureAsync(message, invalidFields, ct);
        }

        try
        {
            await identities.CreateUserAsync(new CreateUserRequest
            {
                Username = Create.Username,
                DisplayName = Create.DisplayName,
                Password = Create.Password,
                Role = Create.Role,
                RoleId = Create.RoleId,
                GrantedPermissions = Create.Grants,
            }, ct);
            await authorizationSync.SyncAsync(ct);
            TempData["Message"] = "账号已创建。";
            if (IsAjaxRequest()) return new JsonResult(new { redirectUrl = Url.Page("/Users") });
            return RedirectToPage();
        }
        catch (IdentityOperationException ex)
        {
            var invalidFields = ex.Code == ApiErrorCodes.UsernameExists
                ? new[] { $"{nameof(Create)}.{nameof(UserInput.Username)}" }
                : Array.Empty<string>();
            return await CreateFailureAsync(ex.Message, invalidFields, ct);
        }
    }

    public async Task<IActionResult> OnPostUpdateAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        Edit.Role = Edit.RoleId == AccountRole.AdministratorId ? UserRole.Admin : UserRole.User;
        // 不能把账号升级为管理员；管理员账号本身也只有管理员能编辑（含禁用状态的管理员）。
        if (CurrentUser.Role != UserRole.Admin &&
            (Edit.Role == UserRole.Admin ||
             await identities.GetRoleAsync(Edit.Id, ct) == UserRole.Admin))
        {
            TempData["Error"] = "仅管理员可管理管理员账号。";
            return RedirectToPage();
        }
        try
        {
            await identities.UpdateUserAsync(Edit.Id, new UpdateUserRequest
            {
                DisplayName = Edit.DisplayName,
                Role = Edit.Role,
                RoleId = Edit.RoleId,
                Enabled = Edit.Enabled,
                GrantedPermissions = Edit.Grants,
            }, ct);
            await authorizationSync.SyncAsync(ct);
            TempData["Message"] = "账号与权限已更新。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetPasswordAsync(
        Guid id,
        string password,
        string confirmPassword,
        CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
        {
            TempData["Error"] = "两次输入的新密码不一致。";
            return RedirectToPage();
        }
        // 重置管理员密码等于接管管理员账号，仅管理员可执行（含禁用状态的管理员）。
        if (CurrentUser.Role != UserRole.Admin &&
            await identities.GetRoleAsync(id, ct) == UserRole.Admin)
        {
            TempData["Error"] = "仅管理员可重置管理员密码。";
            return RedirectToPage();
        }
        try
        {
            await identities.ResetPasswordAsync(id, password, ct);
            await authorizationSync.SyncAsync(ct);
            TempData["Message"] = "密码已重置，该用户的设备会话已全部撤销。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        // 删除管理员账号仅管理员可执行（含禁用状态；最后管理员另有 GuardLastAdmin 保护）。
        if (CurrentUser.Role != UserRole.Admin &&
            await identities.GetRoleAsync(id, ct) == UserRole.Admin)
        {
            TempData["Error"] = "仅管理员可删除管理员账号。";
            return RedirectToPage();
        }
        try
        {
            await identities.DeleteUserAsync(id, ct);
            await authorizationSync.SyncAsync(ct);
            TempData["Message"] = "账号已删除。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateRoleAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try { await roleService.CreateAsync(RoleEdit.Name, RoleEdit.Grants, ct); TempData["Message"] = "Role created."; }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateRoleAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try { await roleService.UpdateAsync(RoleEdit.Id, RoleEdit.Name, RoleEdit.Grants, ct); await authorizationSync.SyncAsync(ct); TempData["Message"] = "Role updated."; }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteRoleAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try { await roleService.DeleteAsync(id, ct); TempData["Message"] = "Role deleted."; }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    private async Task<IActionResult> LoadAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        Accounts = await identities.ListUsersAsync(ct);
        RoleDefinitions = await roleService.ListAsync(ct);
        return Page();
    }

    private bool IsAjaxRequest() => string.Equals(
        Request.Headers["X-Requested-With"],
        "XMLHttpRequest",
        StringComparison.OrdinalIgnoreCase);

    private string[] InvalidCreateFields() => ModelState
        .Where(entry =>
            entry.Key.StartsWith(nameof(Create) + ".", StringComparison.OrdinalIgnoreCase) &&
            entry.Value is { Errors.Count: > 0 })
        .Select(entry => entry.Key)
        .ToArray();

    private async Task<IActionResult> CreateFailureAsync(
        string message,
        IReadOnlyCollection<string> invalidFields,
        CancellationToken ct)
    {
        if (IsAjaxRequest())
            return new JsonResult(new { error = message, invalidFields })
            {
                StatusCode = StatusCodes.Status422UnprocessableEntity,
            };

        // 无 JavaScript 回退会重绘页面；保留非敏感合法字段，但绝不把明文密码写回 HTML。
        foreach (var field in invalidFields)
        {
            ModelState.Remove(field);
            if (field.EndsWith("." + nameof(UserInput.Username), StringComparison.OrdinalIgnoreCase))
                Create.Username = string.Empty;
            else if (field.EndsWith("." + nameof(UserInput.DisplayName), StringComparison.OrdinalIgnoreCase))
                Create.DisplayName = string.Empty;
            else if (field.EndsWith("." + nameof(UserInput.Password), StringComparison.OrdinalIgnoreCase))
                Create.Password = string.Empty;
        }
        ModelState.Remove($"{nameof(Create)}.{nameof(UserInput.Password)}");
        Create.Password = string.Empty;
        TempData["Error"] = message;
        Accounts = await identities.ListUsersAsync(ct);
        RoleDefinitions = await roleService.ListAsync(ct);
        return Page();
    }

    private void KeepModelStateEntries(string prefix)
    {
        foreach (var key in ModelState.Keys.Where(key =>
                     !key.Equals(prefix, StringComparison.OrdinalIgnoreCase) &&
                     !key.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)).ToArray())
            ModelState.Remove(key);
    }

    public abstract class PermissionInput
    {
        public bool AccessWebUi { get; set; }
        public bool ManageUsers { get; set; }
        public bool SendNotifications { get; set; }
        public bool SendVoiceMessages { get; set; }
        public bool TeacherComing { get; set; }
        public bool ManageSchedule { get; set; }
        public bool RunExtensions { get; set; }
        public bool MainMenuControl { get; set; }
        public bool PowerControl { get; set; }
        public UserPermissions Grants => (AccessWebUi ? UserPermissions.AccessWebUi : 0) |
            (ManageUsers ? UserPermissions.ManageUsers : 0) |
            (SendNotifications ? UserPermissions.SendNotifications : 0) |
            (SendVoiceMessages ? UserPermissions.SendVoiceMessages : 0) |
            (TeacherComing ? UserPermissions.TeacherComing : 0) |
            (ManageSchedule ? UserPermissions.ManageSchedule : 0) |
            (RunExtensions ? UserPermissions.RunExtensions : 0) |
            (MainMenuControl ? UserPermissions.MainMenuControl : 0) |
            (PowerControl ? UserPermissions.PowerControl : 0);
    }

    public sealed class RoleInput : PermissionInput
    {
        public Guid Id { get; set; }
        [Required, StringLength(40, MinimumLength = 1)] public string Name { get; set; } = string.Empty;
    }

    public sealed class UserInput : PermissionInput
    {
        public Guid Id { get; set; }
        [Required(ErrorMessage = "请输入 ID。")]
        [RegularExpression("^[A-Za-z0-9._-]{3,32}$", ErrorMessage = "ID 需为 3-32 位字母、数字、点、下划线或短横线")]
        public string Username { get; set; } = string.Empty;
        [Required(ErrorMessage = "请输入用户名。")]
        [StringLength(40, MinimumLength = 1, ErrorMessage = "用户名需为 1-40 个字符")]
        public string DisplayName { get; set; } = string.Empty;
        [Required(ErrorMessage = "请输入密码。")]
        [StringLength(128, MinimumLength = 8, ErrorMessage = "密码需为 8-128 个字符")]
        public string Password { get; set; } = string.Empty;
        [EnumDataType(typeof(UserRole), ErrorMessage = "角色无效")]
        public UserRole Role { get; set; } = UserRole.User;
        public Guid? RoleId { get; set; } = AccountRole.StudentId;
        public bool Enabled { get; set; }
    }
}
