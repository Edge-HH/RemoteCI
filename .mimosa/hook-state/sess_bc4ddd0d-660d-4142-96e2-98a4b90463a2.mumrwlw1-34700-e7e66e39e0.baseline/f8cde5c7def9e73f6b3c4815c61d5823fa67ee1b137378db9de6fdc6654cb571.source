using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>班级与分组的 Excel 导入导出：只新建导入、导出覆盖（只增改不删除）与整批校验。</summary>
public sealed class ClassExcelTests
{
    [Fact]
    public async Task Import_CreatesGroupsAndClasses_AndReportsInvalidRows()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();

        using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ClassExcelService>();
        var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await classrooms.CreateAsync("已存在班级");
        await classrooms.CreateGroupAsync("已有分组", null);

        using var workbook = new XLWorkbook();
        var groupSheet = workbook.AddWorksheet("分组");
        WriteHeader(groupSheet, ["分组名称", "上级分组"]);
        groupSheet.Cell(3, 1).Value = "高一年级";
        groupSheet.Cell(4, 1).Value = "高一(1)班";
        groupSheet.Cell(4, 2).Value = "高一年级";
        groupSheet.Cell(5, 1).Value = "高一年级"; // 文件内重复
        groupSheet.Cell(6, 1).Value = "孤立分组";
        groupSheet.Cell(6, 2).Value = "不存在的父分组";

        var classSheet = workbook.AddWorksheet("班级");
        WriteHeader(classSheet, ["班级名称", "分组", "访客访问"]);
        classSheet.Cell(3, 1).Value = "新建甲班";
        classSheet.Cell(3, 2).Value = "高一年级; 高一(1)班";
        classSheet.Cell(3, 3).Value = "是";
        classSheet.Cell(4, 1).Value = "已存在班级";
        classSheet.Cell(4, 2).Value = "已有分组";
        classSheet.Cell(4, 3).Value = "否";
        classSheet.Cell(5, 1).Value = "新建乙班";
        classSheet.Cell(5, 2).Value = "不存在的分组";
        classSheet.Cell(5, 3).Value = "否";

        using var stream = Save(workbook);
        var result = await service.ImportAsync(stream);

        Assert.Equal(2, result.CreatedGroups);
        Assert.Equal(1, result.CreatedClasses);
        Assert.Contains(result.Failures, x => x.Sheet == "分组" && x.RowNumber == 5 && x.Message.Contains("重复"));
        Assert.Contains(result.Failures, x => x.Sheet == "分组" && x.RowNumber == 6 && x.Message.Contains("上级分组"));
        Assert.Contains(result.Failures, x => x.Sheet == "班级" && x.RowNumber == 4 && x.Message.Contains("已存在"));
        Assert.Contains(result.Failures, x => x.Sheet == "班级" && x.RowNumber == 5 && x.Message.Contains("不存在"));

        var created = await db.Classrooms.SingleAsync(x => x.Name == "新建甲班");
        Assert.True(created.VisitorAccessEnabled);
        var groupIds = await db.ClassGroupAssignments
            .Where(x => x.ClassroomId == created.Id)
            .Select(x => x.GroupId)
            .ToListAsync();
        Assert.Equal(2, groupIds.Count);
        Assert.NotNull(await db.ClassGroups.SingleOrDefaultAsync(x => x.Name == "高一(1)班"));
    }

    [Fact]
    public async Task Export_AndOverwrite_AddsAndUpdatesWithoutDeleting()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();

        using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ClassExcelService>();
        var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var groupA = await classrooms.CreateGroupAsync("导出分组甲", null);
        var classA = await classrooms.CreateAsync("导出甲班");
        var classB = await classrooms.CreateAsync("导出乙班");
        await classrooms.SetClassGroupsAsync(classA.Id, [groupA.Id]);
        await classrooms.SetClassGroupsAsync(classB.Id, [groupA.Id]);

        // 按分组范围导出，避免把系统默认班级等无关对象带入本次覆盖导入。
        var export = await service.ExportAsync(groupA.Id);
        using (var exported = new XLWorkbook(new MemoryStream(export.Content)))
        {
            Assert.True(exported.TryGetWorksheet("分组", out var exportedGroups));
            Assert.True(exported.TryGetWorksheet("班级", out var exportedClasses));
            Assert.True(exported.TryGetWorksheet("导出信息", out _));
            Assert.True(exportedGroups.Column(3).IsHidden);
            Assert.True(exportedClasses.Column(5).IsHidden);
            Assert.Equal("系统标识", exportedGroups.Cell(2, 3).GetString());
            Assert.Equal("系统标识", exportedClasses.Cell(2, 5).GetString());
            Assert.Contains(exportedClasses.RowsUsed(), row => row.Cell(1).GetString() == "导出甲班");
        }

        using var workbook = new XLWorkbook(new MemoryStream(export.Content));
        var groupSheet = workbook.Worksheet("分组");
        groupSheet.RowsUsed().Single(row => row.Cell(1).GetString() == "导出分组甲").Cell(1).Value = "导出分组甲改";
        var classSheet = workbook.Worksheet("班级");
        var rowA = classSheet.RowsUsed().Single(row => row.Cell(1).GetString() == "导出甲班");
        rowA.Cell(1).Value = "导出甲班改";
        rowA.Cell(2).Value = "导出分组甲改";
        rowA.Cell(3).Value = "是";
        // 删掉乙班所在行：覆盖导入采用保守策略，不应因此删除乙班。
        classSheet.RowsUsed().Single(row => row.Cell(1).GetString() == "导出乙班").Delete();
        var newRow = classSheet.RowsUsed().Max(row => row.RowNumber()) + 1;
        classSheet.Cell(newRow, 1).Value = "新增丙班";
        classSheet.Cell(newRow, 3).Value = "否";

        using var overwriteStream = Save(workbook);
        var result = await service.OverwriteAsync(overwriteStream);

        Assert.False(result.HasFailures);
        Assert.Equal(1, result.UpdatedGroups);
        Assert.Equal(1, result.UpdatedClasses);
        Assert.Equal(1, result.CreatedClasses);

        var renamed = await db.Classrooms.SingleAsync(x => x.Name == "导出甲班改");
        Assert.True(renamed.VisitorAccessEnabled);
        var renamedGroup = await db.ClassGroups.SingleAsync(x => x.Name == "导出分组甲改");
        var assignments = await db.ClassGroupAssignments
            .Where(x => x.ClassroomId == renamed.Id)
            .Select(x => x.GroupId)
            .ToListAsync();
        Assert.Equal([renamedGroup.Id], assignments);
        Assert.NotNull(await db.Classrooms.SingleOrDefaultAsync(x => x.Name == "导出乙班"));
        Assert.NotNull(await db.Classrooms.SingleOrDefaultAsync(x => x.Name == "新增丙班"));
    }

    [Fact]
    public async Task Overwrite_RejectsNameConflict_AndWritesNothing()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();

        using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<ClassExcelService>();
        var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await classrooms.CreateAsync("冲突甲班");
        await classrooms.CreateAsync("冲突乙班");

        var export = await service.ExportAsync(null);
        using var workbook = new XLWorkbook(new MemoryStream(export.Content));
        var classSheet = workbook.Worksheet("班级");
        classSheet.RowsUsed().Single(row => row.Cell(1).GetString() == "冲突甲班").Cell(1).Value = "冲突乙班";

        using var overwriteStream = Save(workbook);
        var result = await service.OverwriteAsync(overwriteStream);

        Assert.True(result.HasFailures);
        Assert.Contains(result.Failures, x => x.Message.Contains("重复"));
        Assert.NotNull(await db.Classrooms.SingleOrDefaultAsync(x => x.Name == "冲突甲班"));
    }

    private static void WriteHeader(IXLWorksheet sheet, string[] headers)
    {
        sheet.Cell(1, 1).Value = "测试导入";
        for (var index = 0; index < headers.Length; index++)
            sheet.Cell(2, index + 1).Value = headers[index];
    }

    private static MemoryStream Save(XLWorkbook workbook)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }
}
