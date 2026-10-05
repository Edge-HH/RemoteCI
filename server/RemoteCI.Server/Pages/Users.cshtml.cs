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
public sealed class UsersModel(
    UserManager<AppUser> users,
    IdentityCoordinator identities,
    AccountRoleService roleService,
    AuthorizationSyncService authorizationSync,
    VisitorAccessSettings visitorAccess,
    ClassroomService classrooms,
    UserImportService importService,
    MemberExcelService memberExcel,
    IMemoryCache reportCache)
    : WebPageModel(users)
{
    [BindProperty]
    public UserInput Create { get; set; } = new();
    [BindProperty]
    public UserInput Edit { get; set; } = new();
    public IReadOnlyList<UserListItem> Accounts { get; private set; } = [];
    public IReadOnlyList<AccountRoleInfo> RoleDefinitions { get; private set; } = [];
    public IReadOnlyDictionary<Guid, IReadOnlyList<ApiKeyInfo>> ApiKeysByUser { get; private set; } =
        new Dictionary<Guid, IReadOnlyList<ApiKeyInfo>>();
    public ApiKeyCreationResult? CreatedApiKey { get; private set; }
    [BindProperty] public RoleInput RoleEdit { get; set; } = new();
    [BindProperty] public bool AutoEnterVisitorPage { get; set; }

    // 文本批量导入兼容入口：推荐每行 ID,用户名,班级,角色,密码；旧 3 列格式使用默认班级和角色。
    [BindProperty] public string? ImportText { get; set; }
    [BindProperty] public Guid? ImportClassId { get; set; }
    [BindProperty] public Guid? ImportRoleId { get; set; }
    /// <summary>班级列表：新建账号的班级分配与文本导入的默认班级共用同一份数据。</summary>
    public IReadOnlyList<ClassDetail> Classes { get; private set; } = [];
    public IReadOnlyList<ClassGroupInfo> ImportGroups { get; private set; } = [];

    /// <summary>最近一次 Excel 导入结果；完整失败列表通过短时缓存跨重定向传递。</summary>
    public MemberExcelImportResult? ExcelImportResult { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct) => await LoadAsync(ct);

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        Create.Role = Create.RoleId == AccountRole.AdministratorId ? UserRole.Admin : UserRole.User;
        if (await ValidateCreateAsync(ct) is { } validationFailure)
            return validationFailure;

        try
        {
            var created = await identities.CreateUserAsync(new CreateUserRequest
            {
                Username = Create.Username,
                DisplayName = Create.DisplayName,
                Password = Create.Password,
                Role = Create.Role,
                RoleId = Create.RoleId,
                GrantedPermissions = Create.Grants,
            }, ct);
            // 与批量导入、Excel 导入一致：账号保留默认班级成员关系，显式选择的班级另建一条成员关系。
            if (Create.Role != UserRole.Admin &&
                Create.RoleId != AccountRole.TeacherId &&
                Create.ClassId is { } classId)
                await classrooms.AddMemberAsync(classId, created.Id, Create.RoleId ?? AccountRole.StudentId, ct);
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

    private async Task<IActionResult?> ValidateCreateAsync(CancellationToken ct)
    {
        if (Create.Role == UserRole.Admin && CurrentUser.Role != UserRole.Admin)
            return await CreateFailureAsync("仅管理员可创建管理员账号。", Array.Empty<string>(), ct);
        var isTeacher = Create.RoleId == AccountRole.TeacherId;
        // 老师通过课表中的教师姓名动态绑定班级，建号时不分配班级；其他普通账号必须归属某个班级。
        if (Create.Role != UserRole.Admin && !isTeacher && Create.ClassId is null)
            ModelState.AddModelError($"{nameof(Create)}.{nameof(UserInput.ClassId)}", "请为账号分配班级。");
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

        // 班级可能在页面打开后被删除；先校验，避免账号已创建但成员关系写入失败。
        if (Create.Role != UserRole.Admin && !isTeacher && Create.ClassId is { } assignedClassId)
        {
            try
            {
                await classrooms.RequireAsync(assignedClassId, ct);
            }
            catch (IdentityOperationException ex)
            {
                return await CreateFailureAsync(ex.Message, new[] { $"{nameof(Create)}.{nameof(UserInput.ClassId)}" }, ct);
            }
        }
        return null;
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

    public async Task<IActionResult> OnPostVisitorAccessAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        // 班级级“开放访客”开关在班级管理页逐班配置；这里只保留登录页自动进入的全局行为。
        var autoEnter = await visitorAccess.SetAutoEnterAsync(AutoEnterVisitorPage, ct);
        AutoEnterVisitorPage = autoEnter;
        TempData["Message"] = autoEnter
            ? "已开启自动进入访客页。"
            : "已关闭自动进入访客页。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostBatchImportAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            var result = await importService.ImportAsync(ImportText, ImportClassId, ImportRoleId, ct);
            await authorizationSync.SyncAsync(ct);
            TempData[result.Failures.Count == 0 ? "Message" : "Error"] = result.Failures.Count == 0
                ? $"批量导入完成：创建 {result.Created} 个账号。"
                : $"批量导入完成 {result.Created} 个，失败 {result.Failures.Count} 个：{string.Join("；", result.Failures.Take(5))}{(result.Failures.Count > 5 ? "…" : "")}";
        }
        catch (IdentityOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetTemplateAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        var content = await memberExcel.CreateTemplateAsync(ct);
        return File(content, MemberExcelService.ExcelContentType, MemberExcelService.TemplateFileName);
    }

    public async Task<IActionResult> OnGetExportAsync(Guid? groupId, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        try
        {
            var export = await memberExcel.ExportAsync(groupId, ct);
            return File(export.Content, MemberExcelService.ExcelContentType, export.FileName);
        }
        catch (IdentityOperationException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToPage();
        }
    }

    public async Task<IActionResult> OnPostImportExcelAsync(IFormFile? excelFile, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        var bytes = await ReadExcelFileAsync(excelFile, ct);
        if (bytes is null) return RedirectToPage();
        try
        {
            using var stream = new MemoryStream(bytes);
            var result = await memberExcel.ImportAsync(stream, ct);
            if (result.CreatedUsers > 0) await authorizationSync.SyncAsync(ct);
            StoreExcelResult(result);
        }
        catch (IdentityOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostOverwriteExcelAsync(IFormFile? excelFile, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin) return RedirectToPage("/Denied");
        var bytes = await ReadExcelFileAsync(excelFile, ct);
        if (bytes is null) return RedirectToPage();
        try
        {
            using var stream = new MemoryStream(bytes);
            var result = await memberExcel.OverwriteAsync(stream, ct);
            if (!result.HasFailures) await authorizationSync.SyncAsync(ct);
            StoreExcelResult(result);
        }
        catch (IdentityOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage();
    }
    public async Task<IActionResult> OnPostCreateApiKeyAsync(Guid id, string? name, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        // 普通人员管理员不能为目标管理员创建 Key，否则等于接管管理员权限。
        if (CurrentUser.Role != UserRole.Admin && await identities.GetRoleAsync(id, ct) == UserRole.Admin)
            return RedirectToPage("/Denied");
        try
        {
            var created = await identities.CreateApiKeyAsync(id, name, ct);
            StoreCreatedApiKey(created);
            TempData["Message"] = "API Key 已生成，请立即复制；离开页面后无法再次查看。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokeApiKeyAsync(Guid id, Guid keyId, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.ManageUsers) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin && await identities.GetRoleAsync(id, ct) == UserRole.Admin)
            return RedirectToPage("/Denied");
        try
        {
            await identities.RevokeApiKeyAsync(id, keyId, ct);
            TempData["Message"] = "API Key 已吊销。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public IReadOnlyList<ApiKeyInfo> GetApiKeys(Guid userId) =>
        ApiKeysByUser.TryGetValue(userId, out var keys) ? keys : [];

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
        Classes = await classrooms.ListAsync(ct);
        ImportGroups = await classrooms.ListGroupsAsync(ct);
        AutoEnterVisitorPage = await visitorAccess.GetAutoEnterAsync(ct);
        await LoadApiKeyStateAsync(ct);
        if (TempData["MemberExcelReportId"] is string reportId &&
            reportCache.TryGetValue(reportId, out MemberExcelImportResult? report))
        {
            ExcelImportResult = report;
        }
        return Page();
    }

    /// <summary>把完整导入结果放进短时缓存，TempData 只保存一个很小的键，避免 Cookie 超限。</summary>
    private void StoreExcelResult(MemberExcelImportResult result)
    {
        var key = Guid.NewGuid().ToString("N");
        reportCache.Set(key, result, TimeSpan.FromMinutes(10));
        TempData["MemberExcelReportId"] = key;
        TempData[result.HasFailures ? "Error" : "Message"] = result.Summary;
    }

    private async Task<byte[]?> ReadExcelFileAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            TempData["Error"] = "请选择要上传的 .xlsx 文件。";
            return null;
        }
        if (file.Length > 20 * 1024 * 1024)
        {
            TempData["Error"] = "Excel 文件不能超过 20 MB。";
            return null;
        }
        if (!string.Equals(Path.GetExtension(file.FileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = "仅支持 .xlsx 格式的 Excel 文件。";
            return null;
        }

        using var buffer = new MemoryStream();
        await using var stream = file.OpenReadStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
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
        AutoEnterVisitorPage = await visitorAccess.GetAutoEnterAsync(ct);
        await LoadApiKeyStateAsync(ct);
        return Page();
    }

    private async Task LoadApiKeyStateAsync(CancellationToken ct)
    {
        ApiKeysByUser = await identities.ListApiKeysByUsersAsync(Accounts.Select(x => x.Id).ToArray(), ct);
        CreatedApiKey = TakeCreatedApiKey();
    }

    /// <summary>把一次性明文放进服务端内存缓存，TempData 只保存随机引用，避免明文进入客户端 Cookie。</summary>
    private void StoreCreatedApiKey(ApiKeyCreationResult result)
    {
        var id = Guid.NewGuid().ToString("N");
        reportCache.Set(id, result, TimeSpan.FromMinutes(10));
        TempData["ApiKeyRevealId"] = id;
    }

    private ApiKeyCreationResult? TakeCreatedApiKey()
    {
        if (TempData["ApiKeyRevealId"] is not string id ||
            !reportCache.TryGetValue(id, out ApiKeyCreationResult? result)) return null;
        reportCache.Remove(id);
        return result;
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
        public bool ChangeDisplayName { get; set; }
        public bool ApiAccess { get; set; }
        public bool RequestScheduleSwap { get; set; }
        public bool ForceScheduleSwap { get; set; }
        public UserPermissions Grants => (AccessWebUi ? UserPermissions.AccessWebUi : 0) |
            (ManageUsers ? UserPermissions.ManageUsers : 0) |
            (SendNotifications ? UserPermissions.SendNotifications : 0) |
            (SendVoiceMessages ? UserPermissions.SendVoiceMessages : 0) |
            (TeacherComing ? UserPermissions.TeacherComing : 0) |
            (ManageSchedule ? UserPermissions.ManageSchedule : 0) |
            (RunExtensions ? UserPermissions.RunExtensions : 0) |
            (MainMenuControl ? UserPermissions.MainMenuControl : 0) |
            (PowerControl ? UserPermissions.PowerControl : 0) |
            (ApiAccess ? UserPermissions.ApiAccess : 0) |
            (RequestScheduleSwap ? UserPermissions.RequestScheduleSwap : 0) |
            (ForceScheduleSwap ? UserPermissions.ForceScheduleSwap : 0);
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
        /// <summary>非管理员账号的归属班级；管理员账号忽略该字段。</summary>
        public Guid? ClassId { get; set; }
        public bool Enabled { get; set; }
    }
}
