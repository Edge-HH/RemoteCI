using System.Text.Json;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 成员 Excel 批量导入、导出与覆盖导入。普通导入只新增账号；覆盖导入只处理导出文件记录的班级范围，
/// 通过隐藏的“系统标识”识别账号，不读取或修改密码。
/// </summary>
public sealed partial class MemberExcelService(
    AppDbContext db,
    UserManager<AppUser> users,
    IdentityCoordinator identities,
    AccountRoleService roleService,
    ClassroomService classrooms)
{
    public const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string TemplateFileName = "RemoteCI-成员导入模板.xlsx";

    private const string MemberSheetName = "成员";
    private const string ExportInfoSheetName = "导出信息";
    private const string SystemIdHeader = "系统标识";
    private const int HeaderRow = 2;
    private const int MaxTemplateRows = 500;

    private static readonly string[] TemplateHeaders = ["用户 ID", "用户名", "班级", "角色", "密码"];
    private static readonly string[] ExportHeaders = ["用户 ID", "用户名", "班级", "角色", SystemIdHeader];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<byte[]> CreateTemplateAsync(CancellationToken ct = default)
    {
        // SQLite 不支持在 SQL 中按 DateTimeOffset 排序；班级数量很少，取回后再排序。
        var classes = (await db.Classrooms.AsNoTracking()
                .Select(x => x.Name)
                .ToListAsync(ct))
            .OrderBy(x => x, StringComparer.CurrentCulture)
            .ToList();
        var roles = await db.AccountRoles.AsNoTracking()
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.Name)
            .Select(x => x.Name)
            .ToListAsync(ct);
        using var workbook = BuildWorkbook(
            """
            填写须知：
            1. 请勿修改表头结构；每行填写一个班级成员关系。
            2. 用户 ID、用户名、班级、角色必填；密码选填，留空表示首次登录时设置密码。
            3. 用户 ID 为 3-32 位字母、数字、点、下划线或短横线。
            4. 密码为 8-128 个字符；导入不会覆盖已有用户的密码。
            5. 同一用户可填写多行加入多个班级；已存在的用户会在导入结果中列出。
            """,
            TemplateHeaders,
            classes,
            roles,
            includeSystemId: false);
        return SaveWorkbook(workbook);
    }

    public async Task<MemberExcelExport> ExportAsync(Guid? groupId, CancellationToken ct = default)
    {
        var scope = await ResolveScopeAsync(groupId, ct);
        var scopeIds = scope.Select(x => x.Id).ToList();
        var rows = await db.ClassMemberships.AsNoTracking()
            .Where(x => scopeIds.Contains(x.ClassroomId))
            .Join(db.Users.AsNoTracking(), x => x.UserId, y => y.Id, (membership, user) => new { membership, user })
            .Join(db.Classrooms.AsNoTracking(), x => x.membership.ClassroomId, y => y.Id, (item, classroom) => new { item.membership, item.user, classroom })
            .Join(db.AccountRoles.AsNoTracking(), x => x.membership.RoleDefinitionId, y => y.Id, (item, role) => new ExportRow
            {
                UserId = item.user.Id,
                Username = item.user.UserName ?? string.Empty,
                DisplayName = item.user.DisplayName,
                ClassName = item.classroom.Name,
                RoleName = role.Name,
            })
            .ToListAsync(ct);
        rows = rows
            .OrderBy(x => x.ClassName, StringComparer.CurrentCulture)
            .ThenBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var classes = scope.Select(x => x.Name).ToList();
        var roles = await db.AccountRoles.AsNoTracking()
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.Name)
            .Select(x => x.Name)
            .ToListAsync(ct);
        using var workbook = BuildWorkbook(
            """
            此文件由 RemoteCI 导出，可修改后用于“覆盖导入”：
            1. 请勿修改“系统标识”列和“导出信息”工作表；它们用于识别账号和导出范围。
            2. 用户 ID、用户名、班级、角色可修改；密码不在导出文件中，也不会被覆盖。
            3. 删除某一行会移除该成员在导出班级中的对应成员关系；新增行可创建账号或加入班级。
            4. 覆盖导入会整体替换导出范围内的成员关系，请先确认导出范围。
            """,
            ExportHeaders,
            classes,
            roles,
            includeSystemId: true);
        var sheet = workbook.Worksheet(MemberSheetName);
        var rowNumber = HeaderRow + 1;
        foreach (var row in rows)
        {
            sheet.Cell(rowNumber, 1).Value = row.Username;
            sheet.Cell(rowNumber, 2).Value = row.DisplayName;
            sheet.Cell(rowNumber, 3).Value = row.ClassName;
            sheet.Cell(rowNumber, 4).Value = row.RoleName;
            sheet.Cell(rowNumber, 5).Value = row.UserId.ToString();
            rowNumber++;
        }
        sheet.Column(5).Hide();

        var metadata = new ExportMetadata
        {
            Version = 1,
            ClassIds = scopeIds,
            GroupId = groupId,
            GroupName = groupId is null ? "全部班级" : await GetGroupNameAsync(groupId.Value, ct),
            ExportedAt = DateTimeOffset.Now,
        };
        AddExportInfoSheet(workbook, metadata);

        var suffix = groupId is null ? "全部班级" : SanitizeFileName(metadata.GroupName);
        return new MemberExcelExport
        {
            Content = SaveWorkbook(workbook),
            FileName = $"RemoteCI-成员导出-{suffix}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx",
        };
    }
    public async Task<MemberExcelImportResult> ImportAsync(Stream stream, CancellationToken ct = default)
    {
        using var workbook = OpenWorkbook(stream);
        var sheet = RequireMemberSheet(workbook);
        var headers = ReadHeaders(sheet, requireSystemId: false);
        var rows = ReadRows(sheet, headers);
        if (rows.Count == 0)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "Excel 中没有可导入的成员数据。");

        var result = new MemberExcelImportResult { TotalRows = rows.Count };
        var seenLoginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            try
            {
                ValidateRow(row, allowPassword: true, allowSystemId: false);
                if (!seenLoginIds.Add(row.UserId))
                    throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "文件内存在重复的用户 ID。");

                var role = await roleService.FindByNameAsync(row.RoleName, ct)
                    ?? throw new IdentityOperationException(ApiErrorCodes.NotFound, $"角色“{row.RoleName}”不存在。");
                var classroom = await classrooms.EnsureClassByNameAsync(row.ClassName, ct);
                var created = await identities.CreateUserAsync(new CreateUserRequest
                {
                    Username = row.UserId,
                    DisplayName = row.DisplayName,
                    Password = row.Password,
                    Role = UserRole.User,
                    RoleId = role.Id,
                    GrantedPermissions = UserPermissions.None,
                }, ct);
                await classrooms.AddMemberAsync(classroom.Id, created.Id, role.Id, ct);
                result.CreatedUsers++;
            }
            catch (IdentityOperationException ex)
            {
                var message = ex.Code == ApiErrorCodes.UsernameExists
                    ? "用户 ID 已存在，请修改后重试。"
                    : ex.Message;
                result.Failures.Add(new MemberExcelImportFailure(row.RowNumber, row.UserId, message));
            }
            catch (DbUpdateException)
            {
                result.Failures.Add(new MemberExcelImportFailure(row.RowNumber, row.UserId, "用户 ID 已存在，请修改后重试。"));
            }
        }
        return result;
    }

    public async Task<MemberExcelImportResult> OverwriteAsync(Stream stream, CancellationToken ct = default)
    {
        using var workbook = OpenWorkbook(stream);
        var metadata = ReadExportMetadata(workbook);
        var sheet = RequireMemberSheet(workbook);
        var headers = ReadHeaders(sheet, requireSystemId: true);
        var rows = ReadRows(sheet, headers);
        var scopeIds = metadata.ClassIds.Distinct().ToHashSet();
        var result = new MemberExcelImportResult { TotalRows = rows.Count, Overwrite = true };

        var scopeClasses = await db.Classrooms.AsNoTracking()
            .Where(x => scopeIds.Contains(x.Id))
            .ToListAsync(ct);
        if (scopeClasses.Count != scopeIds.Count)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "导出信息中的班级已不存在，请重新导出后再修改。");

        if (rows.Count == 0)
        {
            var existing = await db.ClassMemberships
                .Where(x => scopeIds.Contains(x.ClassroomId))
                .ToListAsync(ct);
            if (existing.Count > 0)
            {
                db.ClassMemberships.RemoveRange(existing);
                await BumpAccountVersionAsync(ct);
                await db.SaveChangesAsync(ct);
                result.RemovedMemberships = existing.Count;
            }
            return result;
        }

        var allClasses = await db.Classrooms.AsNoTracking().ToListAsync(ct);
        var classByName = allClasses.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var allRoles = await db.AccountRoles.AsNoTracking().ToListAsync(ct);
        var roleByKey = new Dictionary<string, AccountRole>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in allRoles)
        {
            roleByKey[role.Name] = role;
            roleByKey[role.NormalizedName] = role;
        }
        // 覆盖导入与新建导入一致：内置“班主任”曾名“班管理员”，旧清单仍按旧名引用。
        if (allRoles.FirstOrDefault(x => x.Id == AccountRole.ClassAdministratorId) is { } legacyClassAdmin)
            roleByKey[AccountRoleService.LegacyClassAdministratorName] = legacyClassAdmin;

        var allUsers = await db.Users.AsNoTracking().ToListAsync(ct);
        var userById = allUsers.ToDictionary(x => x.Id);
        var userByName = allUsers
            .Where(x => !string.IsNullOrWhiteSpace(x.UserName))
            .GroupBy(x => x.UserName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);

        var plans = BuildOverwritePlans(
            rows, scopeIds, classByName, roleByKey, allRoles, userById, userByName, result);

        if (result.Failures.Count > 0)
            return result;
        var desiredKeys = plans
            .SelectMany(x => x.Memberships.Select(m => (x.UserId, m.ClassroomId)))
            .ToHashSet();
        var existingMemberships = await db.ClassMemberships
            .Where(x => scopeIds.Contains(x.ClassroomId))
            .ToListAsync(ct);
        var existingByKey = existingMemberships.ToDictionary(x => (x.UserId, x.ClassroomId));

        result.CreatedUsers = plans.Count(x => x.ExistingUser is null);
        result.UpdatedUsers = plans.Count(x => x.ExistingUser is not null);
        result.RemovedMemberships = existingMemberships.Count(x => !desiredKeys.Contains((x.UserId, x.ClassroomId)));
        result.AddedMemberships = desiredKeys.Count(x => !existingByKey.ContainsKey(x));
        result.UpdatedMemberships = plans
            .SelectMany(plan => plan.Memberships.Select(m => (plan.UserId, m.ClassroomId, m.RoleId)))
            .Count(x => existingByKey.TryGetValue((x.UserId, x.ClassroomId), out var existing) && existing.RoleDefinitionId != x.RoleId);

        await ApplyOverwriteChangesAsync(plans, existingMemberships, existingByKey, desiredKeys, ct);

        return result;
    }

    private static List<OverwritePlan> BuildOverwritePlans(
        IReadOnlyList<ParsedMemberRow> rows,
        IReadOnlySet<Guid> scopeIds,
        IReadOnlyDictionary<string, Classroom> classByName,
        IReadOnlyDictionary<string, AccountRole> roleByKey,
        IReadOnlyList<AccountRole> allRoles,
        IReadOnlyDictionary<Guid, AppUser> userById,
        IReadOnlyDictionary<string, AppUser> userByName,
        MemberExcelImportResult result)
    {
        var plans = new List<OverwritePlan>();
        var seenUserIds = new HashSet<Guid>();
        var seenLoginIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in rows.GroupBy(x => string.IsNullOrWhiteSpace(x.SystemId) ? $"name:{x.UserId}" : $"id:{x.SystemId}", StringComparer.OrdinalIgnoreCase))
        {
            var plan = BuildOverwritePlan(group, scopeIds, classByName, roleByKey, allRoles, userById, userByName, seenUserIds, seenLoginIds, result);
            if (plan is not null)
                plans.Add(plan);
        }
        return plans;
    }

    private static OverwritePlan? BuildOverwritePlan(
        IGrouping<string, ParsedMemberRow> group,
        IReadOnlySet<Guid> scopeIds,
        IReadOnlyDictionary<string, Classroom> classByName,
        IReadOnlyDictionary<string, AccountRole> roleByKey,
        IReadOnlyList<AccountRole> allRoles,
        IReadOnlyDictionary<Guid, AppUser> userById,
        IReadOnlyDictionary<string, AppUser> userByName,
        ISet<Guid> seenUserIds,
        ISet<string> seenLoginIds,
        MemberExcelImportResult result)
    {
        var first = group.First();
        var failures = new List<string>();
        ValidateOverwriteIdentity(group, first, failures);
        var existingUser = FindExistingUser(first, userById, userByName, failures);
        ValidateOverwriteConflicts(first, existingUser, userByName, seenUserIds, seenLoginIds, failures);
        var memberships = BuildMemberships(group, scopeIds, classByName, roleByKey, failures);
        if (failures.Count > 0)
        {
            foreach (var row in group)
                result.Failures.Add(new MemberExcelImportFailure(row.RowNumber, row.UserId, string.Join("；", failures.Distinct())));
            return null;
        }

        var globalRole = GetGlobalRole(memberships, allRoles);
        return new OverwritePlan(existingUser, existingUser?.Id ?? Guid.NewGuid(), first.UserId.Trim(),
            first.DisplayName.Trim(), globalRole, memberships);
    }

    private static void ValidateOverwriteIdentity(
        IGrouping<string, ParsedMemberRow> group,
        ParsedMemberRow first,
        ICollection<string> failures)
    {
        if (group.Any(x => !string.Equals(x.UserId, first.UserId, StringComparison.OrdinalIgnoreCase)))
            failures.Add("同一系统标识的用户 ID 必须保持一致。");
        if (group.Any(x => !string.Equals(x.DisplayName.Trim(), first.DisplayName.Trim(), StringComparison.Ordinal)))
            failures.Add("同一系统标识的用户名必须保持一致。");
    }

    private static void ValidateOverwriteConflicts(
        ParsedMemberRow first,
        AppUser? existingUser,
        IReadOnlyDictionary<string, AppUser> userByName,
        ISet<Guid> seenUserIds,
        ISet<string> seenLoginIds,
        ICollection<string> failures)
    {
        if (existingUser is not null && !seenUserIds.Add(existingUser.Id))
            failures.Add("同一账号在文件中出现多次。");
        var login = first.UserId.Trim();
        if (!seenLoginIds.Add(login)) failures.Add("文件内存在重复的用户 ID。");
        if (userByName.TryGetValue(login, out var loginOwner) && loginOwner.Id != existingUser?.Id)
            failures.Add("用户 ID 已被其他账号使用。");
    }

    private static AccountRole GetGlobalRole(
        IReadOnlyList<PlannedMembership> memberships,
        IReadOnlyList<AccountRole> allRoles) =>
        allRoles.Single(x => x.Id == (memberships.Count > 0 ? memberships[0].RoleId : AccountRole.StudentId));

    private static AppUser? FindExistingUser(
        ParsedMemberRow row,
        IReadOnlyDictionary<Guid, AppUser> userById,
        IReadOnlyDictionary<string, AppUser> userByName,
        ICollection<string> failures)
    {
        if (!string.IsNullOrWhiteSpace(row.SystemId))
        {
            if (!Guid.TryParse(row.SystemId, out var userId) || !userById.TryGetValue(userId, out var user))
                failures.Add("系统标识对应的账号不存在，请重新导出后再修改。");
            else
                return user;
        }
        else if (userByName.TryGetValue(row.UserId, out var matchedUser))
        {
            return matchedUser;
        }
        return null;
    }

    private static List<PlannedMembership> BuildMemberships(
        IGrouping<string, ParsedMemberRow> group,
        IReadOnlySet<Guid> scopeIds,
        IReadOnlyDictionary<string, Classroom> classByName,
        IReadOnlyDictionary<string, AccountRole> roleByKey,
        ICollection<string> failures)
    {
        var memberships = new List<PlannedMembership>();
        var classIds = new HashSet<Guid>();
        foreach (var row in group)
        {
            try { ValidateRow(row, allowPassword: false, allowSystemId: true); }
            catch (IdentityOperationException ex) { failures.Add(ex.Message); continue; }
            if (!classByName.TryGetValue(row.ClassName.Trim(), out var classroom))
            {
                failures.Add($"班级“{row.ClassName}”不存在。");
                continue;
            }
            if (!scopeIds.Contains(classroom.Id))
            {
                failures.Add($"班级“{row.ClassName}”不在本次导出范围内。");
                continue;
            }
            if (!roleByKey.TryGetValue(row.RoleName.Trim(), out var role))
            {
                failures.Add($"角色“{row.RoleName}”不存在。");
                continue;
            }
            if (!classIds.Add(classroom.Id))
            {
                failures.Add($"班级“{row.ClassName}”在同一账号下重复。");
                continue;
            }
            memberships.Add(new PlannedMembership(classroom.Id, role.Id));
        }
        return memberships;
    }

    private async Task ApplyOverwriteChangesAsync(
        IReadOnlyList<OverwritePlan> plans,
        IReadOnlyList<ClassMembership> existingMemberships,
        IReadOnlyDictionary<(Guid UserId, Guid ClassroomId), ClassMembership> existingByKey,
        IReadOnlySet<(Guid UserId, Guid ClassroomId)> desiredKeys,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var metadata = await db.SystemMetadata.SingleAsync(x => x.Id == 1, ct);
            metadata.AccountVersion++;
            var version = metadata.AccountVersion;
            var now = DateTimeOffset.UtcNow;
            foreach (var plan in plans)
                await ApplyUserPlanAsync(plan, version, now, ct);
            foreach (var existing in existingMemberships.Where(x => !desiredKeys.Contains((x.UserId, x.ClassroomId))))
                db.ClassMemberships.Remove(existing);
            foreach (var plan in plans)
                foreach (var membership in plan.Memberships)
                    ApplyMembershipPlan(plan.UserId, membership, existingByKey);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private async Task ApplyUserPlanAsync(OverwritePlan plan, long version, DateTimeOffset now, CancellationToken ct)
    {
        if (plan.ExistingUser is null)
        {
            var user = new AppUser
            {
                Id = plan.UserId, UserName = plan.Username, DisplayName = plan.DisplayName,
                Role = plan.GlobalRole.Kind == AccountRoleKind.Administrator ? UserRole.Admin : UserRole.User,
                RoleDefinitionId = plan.GlobalRole.Id, GrantedPermissions = UserPermissions.None,
                Enabled = true, PasswordPending = true, UpdatedAt = now, Version = version,
            };
            var create = await users.CreateAsync(user);
            EnsureIdentitySucceeded(create.Succeeded, create.Errors);
            return;
        }

        var existing = await db.Users.SingleAsync(x => x.Id == plan.UserId, ct);
        existing.UpdatedAt = now;
        existing.Version = version;
        if (!string.Equals(existing.UserName, plan.Username, StringComparison.OrdinalIgnoreCase))
        {
            var rename = await users.SetUserNameAsync(existing, plan.Username);
            EnsureIdentitySucceeded(rename.Succeeded, rename.Errors);
        }
        existing.DisplayName = plan.DisplayName;
        var update = await users.UpdateAsync(existing);
        EnsureIdentitySucceeded(update.Succeeded, update.Errors);
    }

    private void ApplyMembershipPlan(
        Guid userId,
        PlannedMembership membership,
        IReadOnlyDictionary<(Guid UserId, Guid ClassroomId), ClassMembership> existingByKey)
    {
        if (existingByKey.TryGetValue((userId, membership.ClassroomId), out var existing))
        {
            existing.RoleDefinitionId = membership.RoleId;
            return;
        }
        db.ClassMemberships.Add(new ClassMembership
        {
            UserId = userId,
            ClassroomId = membership.ClassroomId,
            RoleDefinitionId = membership.RoleId,
        });
    }

    private static void EnsureIdentitySucceeded(bool succeeded, IEnumerable<IdentityError> errors)
    {
        if (succeeded) return;
        var duplicate = errors.Any(x => x.Code.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
        throw new IdentityOperationException(
            duplicate ? ApiErrorCodes.UsernameExists : ApiErrorCodes.InvalidRequest,
            string.Join("；", errors.Select(x => x.Description)));
    }

    private async Task<List<Classroom>> ResolveScopeAsync(Guid? groupId, CancellationToken ct)
    {
        if (groupId is null)
        {
            return (await db.Classrooms.AsNoTracking().ToListAsync(ct))
                .OrderBy(x => x.CreatedAt)
                .ToList();
        }

        var classIds = await classrooms.ResolveTargetClassIdsAsync([], [groupId.Value], ct);
        var wanted = classIds.ToHashSet();
        return (await db.Classrooms.AsNoTracking().Where(x => wanted.Contains(x.Id)).ToListAsync(ct))
            .OrderBy(x => x.CreatedAt)
            .ToList();
    }

    private async Task<string> GetGroupNameAsync(Guid groupId, CancellationToken ct) =>
        await db.ClassGroups.AsNoTracking().Where(x => x.Id == groupId).Select(x => x.Name).SingleOrDefaultAsync(ct)
        ?? "指定分组";

    private static XLWorkbook BuildWorkbook(
        string instructions,
        IReadOnlyList<string> headers,
        IReadOnlyList<string> classes,
        IReadOnlyList<string> roles,
        bool includeSystemId)
    {
        var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(MemberSheetName);
        sheet.Cell(1, 1).Value = instructions.Trim();
        sheet.Range(1, 1, 1, headers.Count).Merge();
        sheet.Cell(1, 1).Style.Alignment.WrapText = true;
        sheet.Cell(1, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        sheet.Cell(1, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#F7F8FA");
        sheet.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml("#4B5563");
        sheet.Row(1).Height = 120;

        for (var index = 0; index < headers.Count; index++)
        {
            var cell = sheet.Cell(HeaderRow, index + 1);
            cell.Value = headers[index];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#EAF2FF");
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.BottomBorderColor = XLColor.FromHtml("#B7C9E5");
        }

        sheet.Range(HeaderRow, 1, HeaderRow, headers.Count).SetAutoFilter();
        sheet.SheetView.FreezeRows(HeaderRow);
        sheet.Column(1).Width = 20;
        sheet.Column(2).Width = 18;
        sheet.Column(3).Width = 22;
        sheet.Column(4).Width = 18;
        sheet.Column(5).Width = includeSystemId ? 24 : 20;
        sheet.Column(5).Style.NumberFormat.Format = "@";
        if (includeSystemId)
            sheet.Column(5).Hide();

        AddOptionsAndValidation(workbook, sheet, headers, classes, roles, includeSystemId);
        return workbook;
    }
    private static void AddOptionsAndValidation(
        XLWorkbook workbook,
        IXLWorksheet memberSheet,
        IReadOnlyList<string> headers,
        IReadOnlyList<string> classes,
        IReadOnlyList<string> roles,
        bool includeSystemId)
    {
        if (classes.Count == 0 && roles.Count == 0)
            return;

        var options = workbook.AddWorksheet("选项");
        options.Cell(1, 1).Value = "班级";
        options.Cell(1, 2).Value = "角色";
        for (var index = 0; index < classes.Count; index++)
            options.Cell(index + 2, 1).Value = classes[index];
        for (var index = 0; index < roles.Count; index++)
            options.Cell(index + 2, 2).Value = roles[index];
        options.Hide();

        if (classes.Count > 0)
        {
            var validation = memberSheet.Range(HeaderRow + 1, 3, HeaderRow + MaxTemplateRows, 3).CreateDataValidation();
            validation.List(options.Range(2, 1, classes.Count + 1, 1));
            validation.IgnoreBlanks = true;
            validation.InCellDropdown = true;
            validation.ShowErrorMessage = true;
            validation.ErrorTitle = "班级无效";
            validation.ErrorMessage = "请选择已存在的班级名称。";
        }

        if (roles.Count > 0)
        {
            var validation = memberSheet.Range(HeaderRow + 1, 4, HeaderRow + MaxTemplateRows, 4).CreateDataValidation();
            validation.List(options.Range(2, 2, roles.Count + 1, 2));
            validation.IgnoreBlanks = true;
            validation.InCellDropdown = true;
            validation.ShowErrorMessage = true;
            validation.ErrorTitle = "角色无效";
            validation.ErrorMessage = "请选择已存在的角色名称。";
        }

        if (includeSystemId)
            memberSheet.Column(headers.Count).Hide();
    }

    private static void AddExportInfoSheet(XLWorkbook workbook, ExportMetadata metadata)
    {
        var sheet = workbook.AddWorksheet(ExportInfoSheetName);
        sheet.Cell("A1").Value = "RemoteCI 成员导出信息（请勿修改）";
        sheet.Cell("A2").Value = JsonSerializer.Serialize(metadata, JsonOptions);
        sheet.Column(1).Width = 120;
        sheet.Hide();
    }

    private static ExportMetadata ReadExportMetadata(XLWorkbook workbook)
    {
        var sheet = workbook.Worksheets.FirstOrDefault(x => string.Equals(x.Name, ExportInfoSheetName, StringComparison.Ordinal));
        if (sheet is null)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "覆盖导入仅支持由 RemoteCI 导出的 Excel 文件。");
        var json = sheet.Cell("A2").GetFormattedString();
        ExportMetadata? metadata;
        try
        {
            metadata = JsonSerializer.Deserialize<ExportMetadata>(json, JsonOptions);
        }
        catch (JsonException)
        {
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "导出信息已损坏，请重新导出后再修改。");
        }
        if (metadata is null || metadata.Version != 1)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "导出信息版本不受支持，请重新导出。");
        metadata.ClassIds = (metadata.ClassIds ?? []).Where(x => x != Guid.Empty).Distinct().ToList();
        return metadata;
    }

    private static IXLWorksheet RequireMemberSheet(XLWorkbook workbook) =>
        workbook.Worksheets.FirstOrDefault(x => string.Equals(x.Name, MemberSheetName, StringComparison.Ordinal))
        ?? workbook.Worksheets.FirstOrDefault()
        ?? throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "Excel 缺少可读取的工作表。");

    private static HeaderMap ReadHeaders(IXLWorksheet sheet, bool requireSystemId)
    {
        var lastRow = Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 1, 10);
        var lastColumn = Math.Min(sheet.LastColumnUsed()?.ColumnNumber() ?? 1, 30);
        for (var row = 1; row <= lastRow; row++)
        {
            var map = new HeaderMap(row);
            for (var column = 1; column <= lastColumn; column++)
            {
                var header = NormalizeHeader(sheet.Cell(row, column).GetFormattedString());
                switch (header)
                {
                    case "用户id":
                    case "id":
                        map.UserId = column;
                        break;
                    case "用户名":
                    case "姓名":
                        map.DisplayName = column;
                        break;
                    case "班级":
                    case "所属班级":
                        map.ClassName = column;
                        break;
                    case "角色":
                    case "班级角色":
                        map.RoleName = column;
                        break;
                    case "密码":
                        map.Password = column;
                        break;
                    case "系统标识":
                        map.SystemId = column;
                        break;
                }
            }
            if (map.UserId > 0 && map.DisplayName > 0 && map.ClassName > 0 && map.RoleName > 0)
            {
                if (requireSystemId && map.SystemId <= 0)
                    throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "覆盖导入文件缺少“系统标识”列，请使用 RemoteCI 导出的文件。");
                return map;
            }
        }
        throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "Excel 表头无效，需要包含“用户 ID、用户名、班级、角色”。");
    }

    private static List<ParsedMemberRow> ReadRows(IXLWorksheet sheet, HeaderMap headers)
    {
        var rows = new List<ParsedMemberRow>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? headers.Row;
        for (var rowNumber = headers.Row + 1; rowNumber <= lastRow; rowNumber++)
        {
            var row = new ParsedMemberRow(
                rowNumber,
                CellText(sheet, rowNumber, headers.UserId),
                CellText(sheet, rowNumber, headers.DisplayName),
                CellText(sheet, rowNumber, headers.ClassName),
                CellText(sheet, rowNumber, headers.RoleName),
                CellText(sheet, rowNumber, headers.Password),
                CellText(sheet, rowNumber, headers.SystemId));
            if (string.IsNullOrWhiteSpace(row.UserId) &&
                string.IsNullOrWhiteSpace(row.DisplayName) &&
                string.IsNullOrWhiteSpace(row.ClassName) &&
                string.IsNullOrWhiteSpace(row.RoleName) &&
                string.IsNullOrWhiteSpace(row.Password) &&
                string.IsNullOrWhiteSpace(row.SystemId))
                continue;
            rows.Add(row);
        }
        return rows;
    }

    private static string CellText(IXLWorksheet sheet, int row, int column) =>
        column <= 0 ? string.Empty : sheet.Cell(row, column).GetFormattedString().Trim();

    private static string NormalizeHeader(string value) =>
        value.Trim().Replace(" ", string.Empty).Replace("　", string.Empty).ToLowerInvariant();

    private static void ValidateRow(ParsedMemberRow row, bool allowPassword, bool allowSystemId)
    {
        ValidateIdentityFields(row);
        ValidatePassword(row.Password, allowPassword);
        ValidateSystemId(row.SystemId, allowSystemId);
    }

    private static void ValidateIdentityFields(ParsedMemberRow row)
    {
        if (!UsernameRegex().IsMatch(row.UserId))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "用户 ID 需为 3-32 位字母、数字、点、下划线或短横线。");
        if (string.IsNullOrWhiteSpace(row.DisplayName) || row.DisplayName.Trim().Length > 40)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "用户名需为 1-40 个字符。");
        if (string.IsNullOrWhiteSpace(row.ClassName) || row.ClassName.Trim().Length > 40)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "班级名称需为 1-40 个字符。");
        if (string.IsNullOrWhiteSpace(row.RoleName))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "角色不能为空。");
    }

    private static void ValidatePassword(string password, bool allowPassword)
    {
        if (!allowPassword && !string.IsNullOrWhiteSpace(password))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "覆盖导入不会修改密码，请删除密码列内容。");
        if (allowPassword && !string.IsNullOrWhiteSpace(password) && password.Length is < 8 or > 128)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "密码需为 8-128 个字符。");
    }

    private static void ValidateSystemId(string systemId, bool allowSystemId)
    {
        if (!allowSystemId && !string.IsNullOrWhiteSpace(systemId))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "请使用“覆盖导入”上传 RemoteCI 导出的文件。");
    }

    private static XLWorkbook OpenWorkbook(Stream stream)
    {
        try
        {
            return new XLWorkbook(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "无法读取 Excel 文件，请确认文件为有效的 .xlsx 文件。");
        }
    }

    private static byte[] SaveWorkbook(XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private async Task BumpAccountVersionAsync(CancellationToken ct)
    {
        var metadata = await db.SystemMetadata.SingleAsync(x => x.Id == 1, ct);
        metadata.AccountVersion++;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "指定分组" : result;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{3,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();

    private sealed class ExportRow
    {
        public Guid UserId { get; init; }
        public string Username { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string ClassName { get; init; } = string.Empty;
        public string RoleName { get; init; } = string.Empty;
    }

    private sealed class ExportMetadata
    {
        public int Version { get; set; }
        public List<Guid> ClassIds { get; set; } = [];
        public Guid? GroupId { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public DateTimeOffset ExportedAt { get; set; }
    }

    private sealed record HeaderMap(int Row)
    {
        public int UserId { get; set; }
        public int DisplayName { get; set; }
        public int ClassName { get; set; }
        public int RoleName { get; set; }
        public int Password { get; set; }
        public int SystemId { get; set; }
    }

    private sealed record ParsedMemberRow(
        int RowNumber,
        string UserId,
        string DisplayName,
        string ClassName,
        string RoleName,
        string Password,
        string SystemId);

    private sealed record PlannedMembership(Guid ClassroomId, Guid RoleId);

    private sealed record OverwritePlan(
        AppUser? ExistingUser,
        Guid UserId,
        string Username,
        string DisplayName,
        AccountRole GlobalRole,
        List<PlannedMembership> Memberships);
}

public sealed class MemberExcelExport
{
    public byte[] Content { get; init; } = [];
    public string FileName { get; init; } = string.Empty;
}

public sealed class MemberExcelImportResult
{
    public int TotalRows { get; set; }
    public int CreatedUsers { get; set; }
    public int UpdatedUsers { get; set; }
    public int AddedMemberships { get; set; }
    public int UpdatedMemberships { get; set; }
    public int RemovedMemberships { get; set; }
    public bool Overwrite { get; set; }
    public List<MemberExcelImportFailure> Failures { get; set; } = [];

    public bool HasFailures => Failures.Count > 0;

    public string Summary => HasFailures
        ? Overwrite
            ? $"覆盖导入未执行：发现 {Failures.Count} 条错误，未写入任何变更。"
            : $"导入完成：创建 {CreatedUsers} 个账号，失败 {Failures.Count} 条。"
        : Overwrite
            ? $"覆盖导入完成：新增 {CreatedUsers} 个账号，更新 {UpdatedUsers} 个账号，新增 {AddedMemberships} 条班级关系，更新 {UpdatedMemberships} 条角色，移除 {RemovedMemberships} 条班级关系。"
            : $"导入完成：创建 {CreatedUsers} 个账号。";
}

public sealed record MemberExcelImportFailure(int RowNumber, string Username, string Message);
