using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 班级与分组的 Excel 批量导入、导出与覆盖导入。普通导入只新建班级/分组；覆盖导入按导出文件中的
/// 隐藏“系统标识”识别对象，更新已有班级/分组并新增文件里的新行。采用保守策略：覆盖导入只新增与修改，
/// 删除某一行不会删除对应班级或分组，避免误删班级导致成员关系与插件凭据被级联清理。
/// </summary>
public sealed class ClassExcelService(AppDbContext db, ClassroomService classrooms)
{
    public const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    public const string TemplateFileName = "RemoteCI-班级分组导入模板.xlsx";

    private const string GroupSheetName = "分组";
    private const string ClassSheetName = "班级";
    private const string OptionsSheetName = "选项";
    private const string ExportInfoSheetName = "导出信息";
    private const string SystemIdHeader = "系统标识";
    private const int HeaderRow = 2;
    private const int MaxTemplateRows = 500;

    private static readonly string[] GroupTemplateHeaders = ["分组名称", "上级分组"];
    private static readonly string[] GroupExportHeaders = ["分组名称", "上级分组", SystemIdHeader];
    private static readonly string[] ClassTemplateHeaders = ["班级名称", "分组", "访客访问", "配对码"];
    private static readonly string[] ClassExportHeaders = ["班级名称", "分组", "访客访问", "配对码", SystemIdHeader];
    private static readonly char[] GroupSeparators = [';', '；', ',', '，'];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ---------- 模板 ----------

    public async Task<byte[]> CreateTemplateAsync(CancellationToken ct = default)
    {
        var groups = await LoadGroupsAsync(ct);
        using var workbook = new XLWorkbook();
        BuildGroupSheet(
            workbook,
            """
            填写须知：
            1. 请勿修改表头结构；每行填写一个分组。
            2. “上级分组”留空表示根分组；填写父分组名称即可建立层级。
            3. 分组名称需为 1-40 个字符且不重复；分组最多支持 4 层。
            4. 导入只新建分组；名称已存在的行会作为错误列出，其余合法行仍会创建。
            """,
            GroupTemplateHeaders,
            includeSystemId: false,
            rows: []);
        BuildClassSheet(
            workbook,
            """
            填写须知：
            1. 请勿修改表头结构；每行填写一个班级。
            2. 班级名称需为 1-40 个字符且不重复。
            3. “分组”填写班级所属分组名称，多个分组用分号“;”分隔；留空表示不属于任何分组。
            4. “访客访问”填写“是”或“否”。
            5. “配对码”可选：填写后该班级使用固定配对码，插件用该码连接会自动绑定到本班；留空则不设置。
            6. 导入只新建班级；名称已存在的行会作为错误列出，其余合法行仍会创建。
            """,
            ClassTemplateHeaders,
            includeSystemId: false,
            rows: []);
        AddOptionsAndValidation(workbook, groups.Select(x => x.Name).ToList());
        return SaveWorkbook(workbook);
    }

    // ---------- 导出 ----------

    public async Task<ClassExcelExport> ExportAsync(Guid? groupId, CancellationToken ct = default)
    {
        var groups = await LoadGroupsAsync(ct);
        var groupNameById = groups.ToDictionary(x => x.Id, x => x.Name);
        var scopeClasses = await ResolveScopeAsync(groupId, ct);
        var scopeClassIds = scopeClasses.Select(x => x.Id).ToHashSet();
        var assignments = await db.ClassGroupAssignments.AsNoTracking()
            .Where(x => scopeClassIds.Contains(x.ClassroomId))
            .ToListAsync(ct);
        var groupsByClass = assignments
            .GroupBy(x => x.ClassroomId)
            .ToDictionary(
                x => x.Key,
                x => x.Select(a => groupNameById.GetValueOrDefault(a.GroupId))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .OrderBy(name => name, StringComparer.CurrentCulture)
                    .ToList());

        var scopeName = groupId is { } id
            ? groupNameById.GetValueOrDefault(id) ?? "指定分组"
            : "全部班级";

        using var workbook = new XLWorkbook();
        BuildGroupSheet(
            workbook,
            """
            此文件由 RemoteCI 导出，可修改后用于“覆盖导入”：
            1. 请勿修改“系统标识”列和“导出信息”工作表；它们用于识别对象和导出范围。
            2. 分组名称、上级分组可修改；新增行会创建新分组。
            3. 覆盖导入只新增与修改，不会因为删除某一行而删除分组。
            """,
            GroupExportHeaders,
            includeSystemId: true,
            rows: groups.Select(x => new GroupSheetRow(
                x.Id,
                x.Name,
                x.ParentId is { } parentId ? groupNameById.GetValueOrDefault(parentId) ?? string.Empty : string.Empty)).ToList());
        BuildClassSheet(
            workbook,
            """
            此文件由 RemoteCI 导出，可修改后用于“覆盖导入”：
            1. 请勿修改“系统标识”列和“导出信息”工作表；它们用于识别对象和导出范围。
            2. 班级名称、分组、访客访问可修改；新增行会创建新班级。
            3. “分组”填写班级所属分组名称，多个分组用分号“;”分隔；留空表示不属于任何分组。
            4. “配对码”不回填明文；填写即设置/替换该班固定配对码，留空表示保持现状。
            5. 覆盖导入只新增与修改，不会因为删除某一行而删除班级。
            """,
            ClassExportHeaders,
            includeSystemId: true,
            rows: scopeClasses.Select(x => new ClassSheetRow(
                x.Id,
                x.Name,
                string.Join("; ", groupsByClass.GetValueOrDefault(x.Id) ?? []),
                x.VisitorAccessEnabled,
                // 固定配对码只以哈希存储，导出不回填明文；“覆盖导入”填写即设置/替换，留空表示保持现状。
                string.Empty)).ToList());
        AddOptionsAndValidation(workbook, groups.Select(x => x.Name).ToList());
        AddExportInfoSheet(workbook, new ExportMetadata
        {
            Version = 1,
            GroupId = groupId,
            GroupName = groupId is null ? string.Empty : scopeName,
            ExportedAt = DateTimeOffset.UtcNow,
        });
        return new ClassExcelExport
        {
            Content = SaveWorkbook(workbook),
            FileName = $"RemoteCI-班级分组导出-{SanitizeFileName(scopeName)}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx",
        };
    }

    // ---------- 导入（只新建） ----------

    public async Task<ClassExcelImportResult> ImportAsync(Stream stream, CancellationToken ct = default)
    {
        using var workbook = OpenWorkbook(stream);
        var groupSheet = RequireSheet(workbook, GroupSheetName);
        var classSheet = RequireSheet(workbook, ClassSheetName);
        var groupHeaders = ReadGroupHeaders(groupSheet, requireSystemId: false);
        var classHeaders = ReadClassHeaders(classSheet, requireSystemId: false);
        var groupRows = ReadGroupRows(groupSheet, groupHeaders);
        var classRows = ReadClassRows(classSheet, classHeaders);
        if (groupRows.Count == 0 && classRows.Count == 0)
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "Excel 中没有可导入的班级或分组数据。");

        var result = new ClassExcelImportResult
        {
            TotalGroupRows = groupRows.Count,
            TotalClassRows = classRows.Count,
        };

        var allGroups = await db.ClassGroups.AsNoTracking().ToListAsync(ct);
        var groupByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in allGroups)
            groupByName[group.Name] = group.Id;

        // 分组可能带层级：多轮处理，先创建父分组已经存在（数据库或本文件较早行）的行，直到没有进展。
        var pending = new List<GroupRow>(groupRows);
        var seenGroupNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (pending.Count > 0)
        {
            var progressed = false;
            var remaining = new List<GroupRow>();
            foreach (var row in pending)
            {
                var name = row.Name.Trim();
                var parentName = row.ParentName.Trim();
                if (parentName.Length > 0 && !groupByName.ContainsKey(parentName))
                {
                    // 父分组可能定义在后面的行，先跳过，下一轮再试。
                    remaining.Add(row);
                    continue;
                }

                progressed = true;
                try
                {
                    if (name.Length is < 1 or > 40)
                        throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "分组名称需为 1-40 个字符。");
                    if (!seenGroupNames.Add(name))
                        throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "文件内存在重复的分组名称。");
                    if (groupByName.ContainsKey(name))
                        throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "分组名称已存在。");

                    var parentId = parentName.Length > 0 ? groupByName[parentName] : (Guid?)null;
                    var created = await classrooms.CreateGroupAsync(name, parentId, ct);
                    groupByName[name] = created.Id;
                    result.CreatedGroups++;
                }
                catch (IdentityOperationException ex)
                {
                    result.Failures.Add(new ClassExcelImportFailure(GroupSheetName, row.RowNumber, name, ex.Message));
                }
            }

            if (!progressed)
            {
                foreach (var row in remaining)
                    result.Failures.Add(new ClassExcelImportFailure(GroupSheetName, row.RowNumber, row.Name.Trim(), "上级分组不存在或存在循环引用。"));
                break;
            }
            pending = remaining;
        }

        var classNames = new HashSet<string>(
            await db.Classrooms.AsNoTracking().Select(x => x.Name).ToListAsync(ct),
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in classRows)
        {
            var name = row.Name.Trim();
            try
            {
                if (name.Length is < 1 or > 40)
                    throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "班级名称需为 1-40 个字符。");
                if (classNames.Contains(name))
                    throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "班级名称已存在。");
                var visitorAccess = ParseVisitorAccess(row.VisitorText);
                var groupIds = ResolveGroupIds(row.GroupsText, groupByName);
                var pairCode = row.PairCode.Trim();
                ValidatePairCode(pairCode);

                var created = await classrooms.CreateAsync(name, ct);
                if (visitorAccess)
                    await classrooms.SetVisitorAccessAsync(created.Id, true, ct);
                if (groupIds.Count > 0)
                    await classrooms.SetClassGroupsAsync(created.Id, groupIds, ct);
                if (pairCode.Length > 0)
                    await SetClassPairingCodeAsync(created.Id, pairCode, ct);
                classNames.Add(name);
                result.CreatedClasses++;
            }
            catch (IdentityOperationException ex)
            {
                result.Failures.Add(new ClassExcelImportFailure(ClassSheetName, row.RowNumber, name, ex.Message));
            }
        }

        return result;
    }

    // ---------- 覆盖导入（新增 + 修改，不删除） ----------

    public async Task<ClassExcelImportResult> OverwriteAsync(Stream stream, CancellationToken ct = default)
    {
        using var workbook = OpenWorkbook(stream);
        ReadExportMetadata(workbook); // 确认是 RemoteCI 导出的文件，并保留导出范围信息。
        var groupSheet = RequireSheet(workbook, GroupSheetName);
        var classSheet = RequireSheet(workbook, ClassSheetName);
        var groupHeaders = ReadGroupHeaders(groupSheet, requireSystemId: true);
        var classHeaders = ReadClassHeaders(classSheet, requireSystemId: true);
        var groupRows = ReadGroupRows(groupSheet, groupHeaders);
        var classRows = ReadClassRows(classSheet, classHeaders);

        var result = new ClassExcelImportResult
        {
            TotalGroupRows = groupRows.Count,
            TotalClassRows = classRows.Count,
            Overwrite = true,
        };

        var allGroups = await db.ClassGroups.AsNoTracking().ToListAsync(ct);
        var groupById = allGroups.ToDictionary(x => x.Id);
        var allClasses = await db.Classrooms.AsNoTracking().ToListAsync(ct);
        var classById = allClasses.ToDictionary(x => x.Id);

        var groupPlans = BuildGroupPlans(groupRows, groupById, result);
        if (result.HasFailures)
            return result;
        var idByFinalGroupName = BuildFinalGroupNameMap(allGroups, groupPlans, result);
        if (result.HasFailures)
            return result;
        var parentById = BuildGroupHierarchy(allGroups, groupPlans, idByFinalGroupName, result);
        if (result.HasFailures)
            return result;

        var classPlans = BuildClassPlans(classRows, classById, idByFinalGroupName, result);
        if (result.HasFailures)
            return result;
        ValidateFinalClassNames(allClasses, classPlans, result);
        if (result.HasFailures)
            return result;

        await ApplyOverwriteAsync(groupPlans, classPlans, parentById, result, ct);
        return result;
    }

    private static List<GroupPlan> BuildGroupPlans(
        IReadOnlyList<GroupRow> rows, IReadOnlyDictionary<Guid, ClassGroup> groupById, ClassExcelImportResult result)
    {
        var plans = new List<GroupPlan>();
        var seenIds = new HashSet<Guid>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var name = row.Name.Trim();
            var failures = new List<string>();
            if (name.Length is < 1 or > 40)
                failures.Add("分组名称需为 1-40 个字符。");

            Guid id;
            ClassGroup? existing = null;
            if (!string.IsNullOrWhiteSpace(row.SystemId))
            {
                if (!Guid.TryParse(row.SystemId, out id) || !groupById.TryGetValue(id, out existing))
                {
                    failures.Add("系统标识对应的分组不存在，请重新导出后再修改。");
                    id = Guid.Empty;
                }
                else if (!seenIds.Add(id))
                {
                    failures.Add("同一分组在文件中出现多次。");
                }
            }
            else
            {
                id = Guid.NewGuid();
            }

            if (name.Length is >= 1 and <= 40 && !seenNames.Add(name))
                failures.Add("文件内存在重复的分组名称。");

            if (failures.Count > 0)
            {
                result.Failures.Add(new ClassExcelImportFailure(GroupSheetName, row.RowNumber, name, string.Join("；", failures.Distinct())));
                continue;
            }
            plans.Add(new GroupPlan(existing, id, name, row.ParentName.Trim(), row.RowNumber));
        }
        return plans;
    }

    private static List<ClassPlan> BuildClassPlans(
        IReadOnlyList<ClassRow> rows,
        IReadOnlyDictionary<Guid, Classroom> classById,
        IReadOnlyDictionary<string, Guid> idByGroupName,
        ClassExcelImportResult result)
    {
        var plans = new List<ClassPlan>();
        var seenIds = new HashSet<Guid>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var name = row.Name.Trim();
            var failures = new List<string>();
            if (name.Length is < 1 or > 40)
                failures.Add("班级名称需为 1-40 个字符。");

            Guid id;
            Classroom? existing = null;
            if (!string.IsNullOrWhiteSpace(row.SystemId))
            {
                if (!Guid.TryParse(row.SystemId, out id) || !classById.TryGetValue(id, out existing))
                {
                    failures.Add("系统标识对应的班级不存在，请重新导出后再修改。");
                    id = Guid.Empty;
                }
                else if (!seenIds.Add(id))
                {
                    failures.Add("同一班级在文件中出现多次。");
                }
            }
            else
            {
                id = Guid.NewGuid();
            }

            if (name.Length is >= 1 and <= 40 && !seenNames.Add(name))
                failures.Add("文件内存在重复的班级名称。");

            var visitorAccess = false;
            try
            {
                visitorAccess = ParseVisitorAccess(row.VisitorText);
            }
            catch (IdentityOperationException ex)
            {
                failures.Add(ex.Message);
            }

            var groupIds = new List<Guid>();
            foreach (var groupName in SplitGroups(row.GroupsText))
            {
                if (!idByGroupName.TryGetValue(groupName, out var groupId))
                    failures.Add($"分组“{groupName}”不存在。");
                else
                    groupIds.Add(groupId);
            }

            // 配对码可留空；填写时必须是 6-64 个不含空白的字符，与固定配对码规则保持一致。
            var pairCode = row.PairCode.Trim();
            if (pairCode.Length > 0 && (pairCode.Length is < 6 or > 64 || pairCode.Any(char.IsWhiteSpace)))
                failures.Add("配对码需为 6-64 个不含空白的字符。");

            if (failures.Count > 0)
            {
                result.Failures.Add(new ClassExcelImportFailure(ClassSheetName, row.RowNumber, name, string.Join("；", failures.Distinct())));
                continue;
            }
            plans.Add(new ClassPlan(
                existing, id, name, visitorAccess, groupIds.Distinct().ToList(), row.RowNumber,
                pairCode.Length == 0 ? null : pairCode));
        }
        return plans;
    }

    /// <summary>把现有名称与计划名称合并成“最终名称 → Id”表，并报告重名冲突。</summary>
    private static Dictionary<string, Guid> BuildFinalGroupNameMap(
        IReadOnlyList<ClassGroup> allGroups, IReadOnlyList<GroupPlan> plans, ClassExcelImportResult result)
    {
        var finalNameById = new Dictionary<Guid, string>();
        foreach (var group in allGroups)
            finalNameById[group.Id] = group.Name;
        foreach (var plan in plans)
            finalNameById[plan.Id] = plan.Name;

        var conflicts = finalNameById
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(x => x.Key))
            .ToHashSet();
        foreach (var plan in plans.Where(x => conflicts.Contains(x.Id)))
            result.Failures.Add(new ClassExcelImportFailure(GroupSheetName, plan.RowNumber, plan.Name, "名称与其他分组重复。"));

        var idByName = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, name) in finalNameById)
            idByName.TryAdd(name, id);
        return idByName;
    }

    private static Dictionary<Guid, Guid?> BuildGroupHierarchy(
        IReadOnlyList<ClassGroup> allGroups,
        IReadOnlyList<GroupPlan> plans,
        IReadOnlyDictionary<string, Guid> idByGroupName,
        ClassExcelImportResult result)
    {
        var parentById = new Dictionary<Guid, Guid?>();
        foreach (var group in allGroups)
            parentById[group.Id] = group.ParentId;

        foreach (var plan in plans)
        {
            if (plan.ParentName.Length == 0)
            {
                parentById[plan.Id] = null;
                continue;
            }
            if (!idByGroupName.TryGetValue(plan.ParentName, out var parentId))
            {
                result.Failures.Add(new ClassExcelImportFailure(GroupSheetName, plan.RowNumber, plan.Name, $"上级分组“{plan.ParentName}”不存在。"));
                continue;
            }
            if (parentId == plan.Id)
            {
                result.Failures.Add(new ClassExcelImportFailure(GroupSheetName, plan.RowNumber, plan.Name, "不能把分组设置为自己的上级。"));
                continue;
            }
            parentById[plan.Id] = parentId;
        }

        if (result.HasFailures)
            return parentById;

        foreach (var plan in plans)
        {
            if (CheckGroupHierarchy(parentById, plan.Id) is { } message)
                result.Failures.Add(new ClassExcelImportFailure(GroupSheetName, plan.RowNumber, plan.Name, message));
        }
        return parentById;
    }

    /// <summary>从某分组向上追溯，检查循环引用与层级深度；返回 null 表示合法。</summary>
    private static string? CheckGroupHierarchy(IReadOnlyDictionary<Guid, Guid?> parentById, Guid id)
    {
        var seen = new HashSet<Guid> { id };
        if (!parentById.TryGetValue(id, out var current))
            return "上级分组不存在。";
        var depth = 1;
        while (current is { } parentId)
        {
            if (!seen.Add(parentId))
                return "分组层级存在循环引用。";
            depth++;
            if (depth > ClassGroup.MaxDepth)
                return $"分组最多支持 {ClassGroup.MaxDepth} 层。";
            if (!parentById.TryGetValue(parentId, out current))
                return "上级分组不存在。";
        }
        return null;
    }

    private static void ValidateFinalClassNames(
        IReadOnlyList<Classroom> allClasses, IReadOnlyList<ClassPlan> plans, ClassExcelImportResult result)
    {
        var finalNameById = new Dictionary<Guid, string>();
        foreach (var classroom in allClasses)
            finalNameById[classroom.Id] = classroom.Name;
        foreach (var plan in plans)
            finalNameById[plan.Id] = plan.Name;

        var conflicts = finalNameById
            .GroupBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(x => x.Key))
            .ToHashSet();
        foreach (var plan in plans.Where(x => conflicts.Contains(x.Id)))
            result.Failures.Add(new ClassExcelImportFailure(ClassSheetName, plan.RowNumber, plan.Name, "名称与其他班级重复。"));
    }

    private async Task ApplyOverwriteAsync(
        IReadOnlyList<GroupPlan> groupPlans,
        IReadOnlyList<ClassPlan> classPlans,
        IReadOnlyDictionary<Guid, Guid?> parentById,
        ClassExcelImportResult result,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // 新建分组：按层级从浅到深依次落库，保证父分组先存在。
            var newGroupPlans = groupPlans.Where(x => x.Existing is null).ToList();
            foreach (var plan in newGroupPlans.OrderBy(x => DepthOf(parentById, x.Id)))
            {
                db.ClassGroups.Add(new ClassGroup
                {
                    Id = plan.Id,
                    Name = plan.Name,
                    ParentId = parentById.GetValueOrDefault(plan.Id),
                    CreatedAt = now,
                    UpdatedAt = now,
                });
                await db.SaveChangesAsync(ct);
                result.CreatedGroups++;
            }

            foreach (var plan in groupPlans.Where(x => x.Existing is not null))
            {
                var group = await db.ClassGroups.SingleAsync(x => x.Id == plan.Id, ct);
                group.Name = plan.Name;
                group.ParentId = parentById.GetValueOrDefault(plan.Id);
                group.UpdatedAt = now;
                result.UpdatedGroups++;
            }

            foreach (var plan in classPlans)
            {
                if (plan.Existing is null)
                {
                    db.Classrooms.Add(new Classroom
                    {
                        Id = plan.Id,
                        Name = plan.Name,
                        VisitorAccessEnabled = plan.VisitorAccess,
                        CreatedAt = now,
                        UpdatedAt = now,
                    });
                    result.CreatedClasses++;
                }
                else
                {
                    var classroom = await db.Classrooms.SingleAsync(x => x.Id == plan.Id, ct);
                    classroom.Name = plan.Name;
                    classroom.VisitorAccessEnabled = plan.VisitorAccess;
                    classroom.UpdatedAt = now;
                    result.UpdatedClasses++;
                }
            }
            await db.SaveChangesAsync(ct);

            // 固定配对码：仅当覆盖文件里填写了明文时才设置/替换，留空表示保持现状。
            foreach (var plan in classPlans.Where(x => x.PairCode is not null))
                await SetClassPairingCodeAsync(plan.Id, plan.PairCode!, ct);

            // 班级分组归属按“分组”列整体更新；只增删该班级的归属关系，不影响班级本身。
            var plannedClassIds = classPlans.Select(x => x.Id).ToHashSet();
            var existingAssignments = await db.ClassGroupAssignments
                .Where(x => plannedClassIds.Contains(x.ClassroomId))
                .ToListAsync(ct);
            foreach (var plan in classPlans)
            {
                var current = existingAssignments.Where(x => x.ClassroomId == plan.Id).ToList();
                var currentIds = current.Select(x => x.GroupId).ToHashSet();
                var wanted = plan.GroupIds.ToHashSet();
                db.ClassGroupAssignments.RemoveRange(current.Where(x => !wanted.Contains(x.GroupId)));
                db.ClassGroupAssignments.AddRange(wanted
                    .Where(groupId => !currentIds.Contains(groupId))
                    .Select(groupId => new ClassGroupAssignment { ClassroomId = plan.Id, GroupId = groupId }));
            }
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    private static int DepthOf(IReadOnlyDictionary<Guid, Guid?> parentById, Guid id)
    {
        var depth = 1;
        var seen = new HashSet<Guid> { id };
        var current = parentById.GetValueOrDefault(id);
        while (current is { } parentId && seen.Add(parentId))
        {
            depth++;
            current = parentById.GetValueOrDefault(parentId);
        }
        return depth;
    }

    // ---------- 工作簿构建 ----------

    private static void BuildGroupSheet(
        XLWorkbook workbook, string instructions, string[] headers, bool includeSystemId, IReadOnlyList<GroupSheetRow> rows)
    {
        var sheet = workbook.AddWorksheet(GroupSheetName);
        WriteHeader(sheet, instructions, headers);
        sheet.Column(1).Width = 24;
        sheet.Column(2).Width = 24;
        if (includeSystemId)
        {
            sheet.Column(3).Width = 24;
            sheet.Column(3).Style.NumberFormat.Format = "@";
            sheet.Column(3).Hide();
        }
        for (var index = 0; index < rows.Count; index++)
        {
            var rowNumber = HeaderRow + 1 + index;
            sheet.Cell(rowNumber, 1).Value = rows[index].Name;
            sheet.Cell(rowNumber, 2).Value = rows[index].ParentName;
            if (includeSystemId)
                sheet.Cell(rowNumber, 3).Value = rows[index].Id.ToString();
        }
    }

    private static void BuildClassSheet(
        XLWorkbook workbook, string instructions, string[] headers, bool includeSystemId, IReadOnlyList<ClassSheetRow> rows)
    {
        var sheet = workbook.AddWorksheet(ClassSheetName);
        WriteHeader(sheet, instructions, headers);
        sheet.Column(1).Width = 24;
        sheet.Column(2).Width = 30;
        sheet.Column(3).Width = 12;
        sheet.Column(4).Width = 20;
        if (includeSystemId)
        {
            sheet.Column(5).Width = 24;
            sheet.Column(5).Style.NumberFormat.Format = "@";
            sheet.Column(5).Hide();
        }
        for (var index = 0; index < rows.Count; index++)
        {
            var rowNumber = HeaderRow + 1 + index;
            sheet.Cell(rowNumber, 1).Value = rows[index].Name;
            sheet.Cell(rowNumber, 2).Value = rows[index].GroupsText;
            sheet.Cell(rowNumber, 3).Value = rows[index].VisitorAccess ? "是" : "否";
            sheet.Cell(rowNumber, 4).Value = rows[index].PairCode;
            if (includeSystemId)
                sheet.Cell(rowNumber, 5).Value = rows[index].Id.ToString();
        }
    }

    private static void WriteHeader(IXLWorksheet sheet, string instructions, string[] headers)
    {
        sheet.Cell(1, 1).Value = instructions.Trim();
        sheet.Range(1, 1, 1, headers.Length).Merge();
        sheet.Cell(1, 1).Style.Alignment.WrapText = true;
        sheet.Cell(1, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
        sheet.Cell(1, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#F7F8FA");
        sheet.Cell(1, 1).Style.Font.FontColor = XLColor.FromHtml("#4B5563");
        sheet.Row(1).Height = 120;

        for (var index = 0; index < headers.Length; index++)
        {
            var cell = sheet.Cell(HeaderRow, index + 1);
            cell.Value = headers[index];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#EAF2FF");
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.BottomBorderColor = XLColor.FromHtml("#B7C9E5");
        }

        sheet.Range(HeaderRow, 1, HeaderRow, headers.Length).SetAutoFilter();
        sheet.SheetView.FreezeRows(HeaderRow);
    }

    private static void AddOptionsAndValidation(XLWorkbook workbook, IReadOnlyList<string> groupNames)
    {
        var options = workbook.AddWorksheet(OptionsSheetName);
        options.Cell(1, 1).Value = "分组";
        options.Cell(1, 2).Value = "访客访问";
        for (var index = 0; index < groupNames.Count; index++)
            options.Cell(index + 2, 1).Value = groupNames[index];
        options.Cell(2, 2).Value = "是";
        options.Cell(3, 2).Value = "否";
        options.Hide();

        if (groupNames.Count > 0)
        {
            var groupSheet = workbook.Worksheet(GroupSheetName);
            var parentValidation = groupSheet.Range(HeaderRow + 1, 2, HeaderRow + MaxTemplateRows, 2).CreateDataValidation();
            parentValidation.List(options.Range(2, 1, groupNames.Count + 1, 1));
            parentValidation.IgnoreBlanks = true;
            parentValidation.InCellDropdown = true;
            parentValidation.ShowErrorMessage = true;
            parentValidation.ErrorTitle = "上级分组无效";
            parentValidation.ErrorMessage = "请选择已存在的分组名称，或留空表示根分组。";
        }

        var classSheet = workbook.Worksheet(ClassSheetName);
        var visitorValidation = classSheet.Range(HeaderRow + 1, 3, HeaderRow + MaxTemplateRows, 3).CreateDataValidation();
        visitorValidation.List(options.Range(2, 2, 3, 2));
        visitorValidation.IgnoreBlanks = true;
        visitorValidation.InCellDropdown = true;
        visitorValidation.ShowErrorMessage = true;
        visitorValidation.ErrorTitle = "访客访问无效";
        visitorValidation.ErrorMessage = "请选择“是”或“否”。";
    }

    private static void AddExportInfoSheet(XLWorkbook workbook, ExportMetadata metadata)
    {
        var sheet = workbook.AddWorksheet(ExportInfoSheetName);
        sheet.Cell("A1").Value = "RemoteCI 班级分组导出信息（请勿修改）";
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
        return metadata;
    }

    // ---------- 解析 ----------

    private static IXLWorksheet RequireSheet(XLWorkbook workbook, string name) =>
        workbook.Worksheets.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.Ordinal))
        ?? throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, $"Excel 缺少“{name}”工作表。");

    private static GroupHeaderMap ReadGroupHeaders(IXLWorksheet sheet, bool requireSystemId)
    {
        var lastRow = Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 1, 10);
        var lastColumn = Math.Min(sheet.LastColumnUsed()?.ColumnNumber() ?? 1, 30);
        for (var row = 1; row <= lastRow; row++)
        {
            var map = new GroupHeaderMap(row);
            for (var column = 1; column <= lastColumn; column++)
            {
                switch (NormalizeHeader(sheet.Cell(row, column).GetFormattedString()))
                {
                    case "分组名称":
                    case "分组":
                    case "名称":
                        map.Name = column;
                        break;
                    case "上级分组":
                    case "父分组":
                    case "上级":
                        map.Parent = column;
                        break;
                    case "系统标识":
                    case "id":
                        map.SystemId = column;
                        break;
                }
            }
            if (map.Name > 0)
            {
                if (requireSystemId && map.SystemId <= 0)
                    throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "覆盖导入文件缺少“系统标识”列，请使用 RemoteCI 导出的文件。");
                return map;
            }
        }
        throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "“分组”工作表表头无效，需要包含“分组名称”。");
    }

    private static ClassHeaderMap ReadClassHeaders(IXLWorksheet sheet, bool requireSystemId)
    {
        var lastRow = Math.Min(sheet.LastRowUsed()?.RowNumber() ?? 1, 10);
        var lastColumn = Math.Min(sheet.LastColumnUsed()?.ColumnNumber() ?? 1, 30);
        for (var row = 1; row <= lastRow; row++)
        {
            var map = new ClassHeaderMap(row);
            for (var column = 1; column <= lastColumn; column++)
            {
                switch (NormalizeHeader(sheet.Cell(row, column).GetFormattedString()))
                {
                    case "班级名称":
                    case "班级":
                    case "名称":
                        map.Name = column;
                        break;
                    case "分组":
                    case "所属分组":
                        map.Groups = column;
                        break;
                    case "访客访问":
                    case "访客":
                        map.Visitor = column;
                        break;
                    case "配对码":
                    case "连接码":
                    case "插件配对码":
                        map.PairCode = column;
                        break;
                    case "系统标识":
                    case "id":
                        map.SystemId = column;
                        break;
                }
            }
            if (map.Name > 0)
            {
                if (requireSystemId && map.SystemId <= 0)
                    throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "覆盖导入文件缺少“系统标识”列，请使用 RemoteCI 导出的文件。");
                return map;
            }
        }
        throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "“班级”工作表表头无效，需要包含“班级名称”。");
    }

    private static List<GroupRow> ReadGroupRows(IXLWorksheet sheet, GroupHeaderMap headers)
    {
        var rows = new List<GroupRow>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? headers.Row;
        for (var rowNumber = headers.Row + 1; rowNumber <= lastRow; rowNumber++)
        {
            var row = new GroupRow(
                rowNumber,
                CellText(sheet, rowNumber, headers.Name),
                CellText(sheet, rowNumber, headers.Parent),
                CellText(sheet, rowNumber, headers.SystemId));
            if (row.Name.Length == 0 && row.ParentName.Length == 0 && row.SystemId.Length == 0)
                continue;
            rows.Add(row);
        }
        return rows;
    }

    private static List<ClassRow> ReadClassRows(IXLWorksheet sheet, ClassHeaderMap headers)
    {
        var rows = new List<ClassRow>();
        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? headers.Row;
        for (var rowNumber = headers.Row + 1; rowNumber <= lastRow; rowNumber++)
        {
            var row = new ClassRow(
                rowNumber,
                CellText(sheet, rowNumber, headers.Name),
                CellText(sheet, rowNumber, headers.Groups),
                CellText(sheet, rowNumber, headers.Visitor),
                CellText(sheet, rowNumber, headers.SystemId),
                CellText(sheet, rowNumber, headers.PairCode));
            if (row.Name.Length == 0 && row.GroupsText.Length == 0 && row.VisitorText.Length == 0 &&
                row.SystemId.Length == 0 && row.PairCode.Length == 0)
                continue;
            rows.Add(row);
        }
        return rows;
    }

    private static string CellText(IXLWorksheet sheet, int row, int column) =>
        column <= 0 ? string.Empty : sheet.Cell(row, column).GetFormattedString().Trim();

    private static string NormalizeHeader(string value) =>
        value.Trim().Replace(" ", string.Empty).Replace("\u3000", string.Empty).ToLowerInvariant();

    private static bool ParseVisitorAccess(string value) => value.Trim().ToLowerInvariant() switch
    {
        "" or "否" or "false" or "0" or "禁用" or "no" or "n" => false,
        "是" or "true" or "1" or "启用" or "yes" or "y" => true,
        _ => throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "“访客访问”只能填写“是”或“否”。"),
    };

    private static List<Guid> ResolveGroupIds(string groupsText, IReadOnlyDictionary<string, Guid> groupByName)
    {
        var ids = new List<Guid>();
        foreach (var groupName in SplitGroups(groupsText))
        {
            if (!groupByName.TryGetValue(groupName, out var groupId))
                throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, $"分组“{groupName}”不存在。");
            if (!ids.Contains(groupId))
                ids.Add(groupId);
        }
        return ids;
    }

    private static IEnumerable<string> SplitGroups(string value) =>
        value.Split(GroupSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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

    private async Task<IReadOnlyList<ClassGroup>> LoadGroupsAsync(CancellationToken ct) =>
        (await db.ClassGroups.AsNoTracking().ToListAsync(ct))
            .OrderBy(x => x.CreatedAt)
            .ToList();

    private async Task<IReadOnlyList<Classroom>> ResolveScopeAsync(Guid? groupId, CancellationToken ct)
    {
        if (groupId is null)
            return (await db.Classrooms.AsNoTracking().ToListAsync(ct))
                .OrderBy(x => x.CreatedAt)
                .ToList();

        var classIds = await classrooms.ResolveTargetClassIdsAsync([], [groupId.Value], ct);
        var wanted = classIds.ToHashSet();
        return (await db.Classrooms.AsNoTracking().Where(x => wanted.Contains(x.Id)).ToListAsync(ct))
            .OrderBy(x => x.CreatedAt)
            .ToList();
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "班级分组" : result;
    }

    /// <summary>与固定配对码一致的校验规则：6-64 个字符且不含空白；空字符串表示“不设置”。</summary>
    private static void ValidatePairCode(string pairCode)
    {
        if (pairCode.Length == 0) return;
        if (pairCode.Length is < 6 or > 64 || pairCode.Any(char.IsWhiteSpace))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "配对码需为 6-64 个不含空白的字符。");
    }

    /// <summary>
    /// 设置班级固定配对码：同一班级只保留一个固定码，重复设置会替换旧码。
    /// 哈希算法与 IdentityCoordinator 保持一致（SHA-256 十六进制小写）。
    /// </summary>
    private async Task SetClassPairingCodeAsync(Guid classroomId, string code, CancellationToken ct)
    {
        var codeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();
        if (await db.PluginPairingCodes.AnyAsync(x => x.CodeHash == codeHash && x.ExpiresAt > DateTimeOffset.UtcNow, ct))
            throw new IdentityOperationException(ApiErrorCodes.InvalidRequest, "配对码已被其他班级使用，请换一个值。");
        await db.PluginPairingCodes
            .Where(x => x.ClassroomId == classroomId && x.IsPersistent)
            .ExecuteDeleteAsync(ct);
        db.PluginPairingCodes.Add(new PluginPairingCode
        {
            Id = Guid.NewGuid(),
            CodeHash = codeHash,
            ClassroomId = classroomId,
            IsPersistent = true,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.MaxValue,
        });
        await db.SaveChangesAsync(ct);
    }

    private sealed record GroupSheetRow(Guid Id, string Name, string ParentName);

    private sealed record ClassSheetRow(Guid Id, string Name, string GroupsText, bool VisitorAccess, string PairCode);

    private sealed record GroupRow(int RowNumber, string Name, string ParentName, string SystemId);

    private sealed record ClassRow(int RowNumber, string Name, string GroupsText, string VisitorText, string SystemId, string PairCode);

    private sealed record GroupPlan(ClassGroup? Existing, Guid Id, string Name, string ParentName, int RowNumber);

    private sealed record ClassPlan(
        Classroom? Existing, Guid Id, string Name, bool VisitorAccess, List<Guid> GroupIds, int RowNumber, string? PairCode);

    private sealed record GroupHeaderMap(int Row)
    {
        public int Name { get; set; }
        public int Parent { get; set; }
        public int SystemId { get; set; }
    }

    private sealed record ClassHeaderMap(int Row)
    {
        public int Name { get; set; }
        public int Groups { get; set; }
        public int Visitor { get; set; }
        public int PairCode { get; set; }
        public int SystemId { get; set; }
    }

    private sealed class ExportMetadata
    {
        public int Version { get; set; }
        public Guid? GroupId { get; set; }
        public string GroupName { get; set; } = string.Empty;
        public DateTimeOffset ExportedAt { get; set; }
    }
}

public sealed class ClassExcelExport
{
    public byte[] Content { get; init; } = [];
    public string FileName { get; init; } = string.Empty;
}

public sealed class ClassExcelImportResult
{
    public int TotalGroupRows { get; set; }
    public int TotalClassRows { get; set; }
    public int CreatedGroups { get; set; }
    public int UpdatedGroups { get; set; }
    public int CreatedClasses { get; set; }
    public int UpdatedClasses { get; set; }
    public bool Overwrite { get; set; }
    public List<ClassExcelImportFailure> Failures { get; set; } = [];

    public bool HasFailures => Failures.Count > 0;

    public string Summary => HasFailures
        ? Overwrite
            ? $"覆盖导入未执行：发现 {Failures.Count} 条错误，未写入任何变更。"
            : $"导入完成：新建 {CreatedGroups} 个分组、{CreatedClasses} 个班级，失败 {Failures.Count} 条。"
        : Overwrite
            ? $"覆盖导入完成：新增 {CreatedGroups} 个分组、更新 {UpdatedGroups} 个分组、新增 {CreatedClasses} 个班级、更新 {UpdatedClasses} 个班级。"
            : $"导入完成：新建 {CreatedGroups} 个分组、{CreatedClasses} 个班级。";
}

public sealed record ClassExcelImportFailure(string Sheet, int RowNumber, string Name, string Message);
