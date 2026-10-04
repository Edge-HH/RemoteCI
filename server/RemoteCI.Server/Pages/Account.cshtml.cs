using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class AccountModel(
    UserManager<AppUser> users,
    IdentityCoordinator identities,
    AuthorizationSyncService authorizationSync,
    SignInManager<AppUser> signIn,
    IMemoryCache cache) : WebPageModel(users)
{
    [BindProperty]
    public PasswordInput Password { get; set; } = new();

    [BindProperty]
    public DisplayNameInput DisplayName { get; set; } = new();

    [BindProperty]
    public CreateApiKeyRequest NewApiKey { get; set; } = new();

    public IReadOnlyList<DeviceSessionSummary> Sessions { get; private set; } = [];
    public IReadOnlyList<ApiKeyInfo> ApiKeys { get; private set; } = [];

    /// <summary>创建成功后仅在本次重定向回显一次；明文不写入数据库或日志。</summary>
    public ApiKeyCreationResult? CreatedApiKey { get; private set; }

    /// <summary>是否显示“修改用户名”表单：仅系统管理员可用。</summary>
    public bool CanChangeDisplayName => CurrentUser.Role == UserRole.Admin;

    /// <summary>是否显示 API Key 管理：学生默认没有，管理员和班管理员默认拥有，也可单独授权。</summary>
    public bool CanUseApi => Permissions.HasFlag(UserPermissions.ApiAccess);

    /// <summary>没有权限但已有历史密钥时仍展示吊销入口，避免遗留凭据无法清理。</summary>
    public bool ShowApiPanel => CanUseApi || ApiKeys.Count > 0;

    /// <summary>
    /// 修改用户名表单的服务端校验信息。该页同时承载修改密码表单，两者共用 ModelState；
    /// 为免互相串错，修改用户名提交时清空 ModelState，只在此属性中呈现本表单的错误。
    /// </summary>
    public string? DisplayNameError { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostPasswordAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        // 页面同时绑定修改密码与修改用户名两个表单，先剔除另一表单的校验项，避免互相干扰。
        KeepModelStateEntries(nameof(Password));
        // 空字段可能不会被复杂类型模型绑定生成条目，显式验证保证必填提示稳定出现。
        TryValidateModel(Password, nameof(Password));
        if (!ModelState.IsValid)
        {
            await LoadAsync(ct);
            return Page();
        }
        try
        {
            await identities.ChangePasswordAsync(CurrentUser.Id, new ChangePasswordRequest
            {
                CurrentPassword = Password.CurrentPassword,
                NewPassword = Password.NewPassword,
            }, ct);
            await authorizationSync.SyncAsync(ct);
            await signIn.SignOutAsync();
            TempData["Message"] = "密码已修改，所有设备会话已撤销，请重新登录。";
            return RedirectToPage("/Login");
        }
        catch (IdentityOperationException ex)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostDisplayNameAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!CanChangeDisplayName)
        {
            TempData["Error"] = "仅系统管理员可以修改用户名。";
            return RedirectToPage();
        }
        // 清空“修改密码”表单模型绑定产生的校验项，避免其错误串入用户名表单（反之亦然）。
        ModelState.Clear();
        var displayName = DisplayName.DisplayName?.Trim() ?? string.Empty;
        if (displayName.Length is < 1 or > 40)
        {
            DisplayNameError = "用户名需为 1-40 个字符";
            await LoadAsync(ct);
            return Page();
        }
        try
        {
            await identities.ChangeDisplayNameAsync(CurrentUser.Id, new ChangeDisplayNameRequest
            {
                DisplayName = displayName,
            }, ct);
            await authorizationSync.SyncAsync(ct);
            TempData["Message"] = "用户名已更新。";
            return RedirectToPage();
        }
        catch (IdentityOperationException ex)
        {
            DisplayNameError = ex.Message;
            await LoadAsync(ct);
            return Page();
        }
    }

    public async Task<IActionResult> OnPostRevokeAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        try
        {
            await identities.RevokeSessionAsync(CurrentUser.Id, id, ct);
            await authorizationSync.SyncAsync(ct);
            TempData["Message"] = "设备会话已撤销。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostCreateApiKeyAsync(CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        if (!Permissions.HasFlag(UserPermissions.ApiAccess))
        {
            TempData["Error"] = "当前账号没有 API 访问权限。";
            return RedirectToPage();
        }
        try
        {
            var created = await identities.CreateApiKeyAsync(CurrentUser.Id, NewApiKey.Name, ct);
            StoreCreatedApiKey(created);
            TempData["Message"] = "API Key 已生成，请立即复制；离开页面后无法再次查看。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeApiKeyAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync() is { } denied) return denied;
        try
        {
            await identities.RevokeApiKeyAsync(CurrentUser.Id, id, ct);
            TempData["Message"] = "API Key 已吊销。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        Sessions = await identities.ListSessionsAsync(CurrentUser.Id, null, ct);
        ApiKeys = await identities.ListApiKeysAsync(CurrentUser.Id, ct);
        CreatedApiKey = TakeCreatedApiKey();
    }

    /// <summary>把一次性明文放进服务端内存缓存，TempData 只保存随机引用，避免明文进入客户端 Cookie。</summary>
    private void StoreCreatedApiKey(ApiKeyCreationResult result)
    {
        var id = Guid.NewGuid().ToString("N");
        cache.Set(id, result, TimeSpan.FromMinutes(10));
        TempData["ApiKeyRevealId"] = id;
    }

    private ApiKeyCreationResult? TakeCreatedApiKey()
    {
        if (TempData["ApiKeyRevealId"] is not string id ||
            !cache.TryGetValue(id, out ApiKeyCreationResult? result)) return null;
        cache.Remove(id);
        return result;
    }

    /// <summary>仅保留指定前缀的绑定校验项；用于同一页面多个独立表单互不干扰。</summary>
    private void KeepModelStateEntries(string prefix)
    {
        foreach (var key in ModelState.Keys.Where(key =>
                     !key.Equals(prefix, StringComparison.OrdinalIgnoreCase) &&
                     !key.StartsWith(prefix + ".", StringComparison.OrdinalIgnoreCase)).ToArray())
            ModelState.Remove(key);
    }

    public sealed class PasswordInput
    {
        [Required, StringLength(128, MinimumLength = 8)]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required, StringLength(128, MinimumLength = 8)]
        public string NewPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "请再次输入新密码。")]
        [Compare(nameof(NewPassword), ErrorMessage = "两次输入的新密码不一致。")]
        public string ConfirmNewPassword { get; set; } = string.Empty;
    }

    public sealed class DisplayNameInput
    {
        [Required(ErrorMessage = "请输入用户名。")]
        [StringLength(40, MinimumLength = 1, ErrorMessage = "用户名需为 1-40 个字符")]
        public string DisplayName { get; set; } = string.Empty;
    }
}
