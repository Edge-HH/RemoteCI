using System.Net;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class MemberExcelTests
{
    [Fact]
    public async Task Import_ReportsDuplicateExistingAndInvalidRows()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();

        using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<MemberExcelService>();
        var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await classrooms.CreateAsync("Excel 导入班");

        using var workbook = CreateWorkbook(
            ["excel.new", "导入新成员", "Excel 导入班", "学生", ""],
            ["excel.new", "重复成员", "Excel 导入班", "学生", ""],
            ["admin", "已存在管理员", "Excel 导入班", "学生", ""],
            ["bad id!", "非法 ID", "Excel 导入班", "学生", ""]);
        using var stream = Save(workbook);

        var result = await service.ImportAsync(stream);

        Assert.Equal(1, result.CreatedUsers);
        Assert.Equal(3, result.Failures.Count);
        Assert.Contains(result.Failures, x => x.RowNumber == 4 && x.Message.Contains("重复"));
        Assert.Contains(result.Failures, x => x.RowNumber == 5 && x.Message.Contains("已存在"));
        Assert.Contains(result.Failures, x => x.RowNumber == 6 && x.Message.Contains("用户 ID"));
        Assert.NotNull(await db.Users.SingleOrDefaultAsync(x => x.UserName == "excel.new"));
    }

    [Fact]
    public async Task Export_OmitsPassword_AndOverwriteUpdatesOnlyExportedScope()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();

        using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<MemberExcelService>();
        var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var classA = await classrooms.CreateAsync("Excel 导出甲班");
        var classB = await classrooms.CreateAsync("Excel 导出乙班");
        var group = await classrooms.CreateGroupAsync("Excel 导出分组", null);
        await classrooms.SetGroupClassesAsync(group.Id, [classA.Id]);

        var userA = await identities.CreateUserAsync(new CreateUserRequest
        {
            ClassId = TestWebApplicationFactory.DefaultClassId,
            Username = "excel.a",
            DisplayName = "导出甲",
            Password = "Excel-A-Password-2026",
            RoleId = AccountRole.StudentId,
        });
        await classrooms.AddMemberAsync(classA.Id, userA.Id, AccountRole.StudentId);
        var userB = await identities.CreateUserAsync(new CreateUserRequest
        {
            ClassId = TestWebApplicationFactory.DefaultClassId,
            Username = "excel.b",
            DisplayName = "导出乙",
            Password = "Excel-B-Password-2026",
            RoleId = AccountRole.StudentId,
        });
        await classrooms.AddMemberAsync(classB.Id, userB.Id, AccountRole.StudentId);

        var export = await service.ExportAsync(group.Id);
        using (var exported = new XLWorkbook(new MemoryStream(export.Content)))
        {
            var sheet = exported.Worksheet("成员");
            Assert.DoesNotContain(sheet.Row(2).CellsUsed(), cell => cell.GetString() == "密码");
            Assert.Equal("系统标识", sheet.Cell(2, 5).GetString());
            Assert.True(sheet.Column(5).IsHidden);
            var rows = sheet.RowsUsed().Skip(2).ToList();
            Assert.Single(rows);
            Assert.Equal("excel.a", rows[0].Cell(1).GetString());
            Assert.DoesNotContain("excel.b", sheet.CellsUsed().Select(x => x.GetString()));
        }

        using var workbook = new XLWorkbook(new MemoryStream(export.Content));
        var edit = workbook.Worksheet("成员");
        var userRow = edit.RowsUsed().Single(x => x.Cell(1).GetString() == "excel.a");
        userRow.Cell(1).Value = "excel.a.renamed";
        userRow.Cell(2).Value = "修改后甲";
        userRow.Cell(4).Value = "班管理员";
        var newRow = edit.RowsUsed().Max(x => x.RowNumber()) + 1;
        edit.Cell(newRow, 1).Value = "excel.c";
        edit.Cell(newRow, 2).Value = "新增丙";
        edit.Cell(newRow, 3).Value = "Excel 导出甲班";
        edit.Cell(newRow, 4).Value = "学生";
        using var overwriteStream = Save(workbook);

        var result = await service.OverwriteAsync(overwriteStream);

        Assert.False(result.HasFailures);
        Assert.Equal(1, result.CreatedUsers);
        Assert.Equal(1, result.UpdatedUsers);
        Assert.Equal(1, result.AddedMemberships);
        Assert.Equal(1, result.UpdatedMemberships);
        Assert.Equal(0, result.RemovedMemberships);

        var updatedA = await db.Users.SingleAsync(x => x.Id == userA.Id);
        Assert.Equal("修改后甲", updatedA.DisplayName);
        Assert.Equal("excel.a.renamed", updatedA.UserName);
        Assert.Equal(AccountRole.ClassAdministratorId, await db.ClassMemberships
            .Where(x => x.UserId == userA.Id && x.ClassroomId == classA.Id)
            .Select(x => x.RoleDefinitionId)
            .SingleAsync());
        Assert.Equal(AccountRole.StudentId, await db.ClassMemberships
            .Where(x => x.UserId == userB.Id && x.ClassroomId == classB.Id)
            .Select(x => x.RoleDefinitionId)
            .SingleAsync());

        var createdC = await db.Users.SingleAsync(x => x.UserName == "excel.c");
        Assert.True(createdC.PasswordPending);
        Assert.True(await db.ClassMemberships.AnyAsync(x => x.UserId == createdC.Id && x.ClassroomId == classA.Id));

        // 覆盖导入没有密码列；已有账号密码保持可用，登录 ID 修改后立即生效。
        await factory.LoginAsync("excel.a.renamed", "Excel-A-Password-2026");
        await Assert.ThrowsAsync<HttpRequestException>(() => factory.LoginAsync("excel.a", "Excel-A-Password-2026"));
    }

    [Fact]
    public async Task Overwrite_InvalidRowKeepsWholeScopeUnchanged()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();

        using var scope = factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<MemberExcelService>();
        var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
        var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var classroom = await classrooms.CreateAsync("Excel 覆盖校验班");
        var user = await identities.CreateUserAsync(new CreateUserRequest
        {
            ClassId = TestWebApplicationFactory.DefaultClassId,
            Username = "excel.keep",
            DisplayName = "保持原名",
            Password = "Excel-Keep-Password-2026",
            RoleId = AccountRole.StudentId,
        });
        await classrooms.AddMemberAsync(classroom.Id, user.Id, AccountRole.StudentId);

        var export = await service.ExportAsync(null);
        using var workbook = new XLWorkbook(new MemoryStream(export.Content));
        var sheet = workbook.Worksheet("成员");
        var row = sheet.RowsUsed().Single(x => x.Cell(1).GetString() == "excel.keep" && x.Cell(3).GetString() == "Excel 覆盖校验班");
        row.Cell(2).Value = string.Empty;
        using var stream = Save(workbook);

        var result = await service.OverwriteAsync(stream);

        Assert.True(result.HasFailures);
        Assert.Equal("保持原名", (await db.Users.SingleAsync(x => x.Id == user.Id)).DisplayName);
        Assert.Equal(AccountRole.StudentId, await db.ClassMemberships
            .Where(x => x.UserId == user.Id && x.ClassroomId == classroom.Id)
            .Select(x => x.RoleDefinitionId)
            .SingleAsync());
    }
    [Fact]
    public async Task WebUi_DownloadsTemplateAndExportWorkbook()
    {
        await using var factory = new TestWebApplicationFactory();
        await factory.LoginAsync();
        using var browser = await CreateLoggedInBrowserAsync(factory);

        var template = await browser.GetAsync("/Users?handler=Template");
        Assert.Equal(HttpStatusCode.OK, template.StatusCode);
        Assert.Equal(MemberExcelService.ExcelContentType, template.Content.Headers.ContentType?.MediaType);
        using (var workbook = new XLWorkbook(new MemoryStream(await template.Content.ReadAsByteArrayAsync())))
        {
            var sheet = workbook.Worksheet("成员");
            Assert.Equal("用户 ID", sheet.Cell(2, 1).GetString());
            Assert.Equal("密码", sheet.Cell(2, 5).GetString());
        }

        var export = await browser.GetAsync("/Users?handler=Export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal(MemberExcelService.ExcelContentType, export.Content.Headers.ContentType?.MediaType);
        using (var workbook = new XLWorkbook(new MemoryStream(await export.Content.ReadAsByteArrayAsync())))
        {
            var sheet = workbook.Worksheet("成员");
            Assert.Equal("系统标识", sheet.Cell(2, 5).GetString());
            Assert.True(sheet.Column(5).IsHidden);
        }
    }

    private static async Task<HttpClient> CreateLoggedInBrowserAsync(TestWebApplicationFactory factory)
    {
        var browser = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
        });
        var html = await browser.GetStringAsync("/Login");
        var token = Regex.Match(
            html,
            "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"",
            RegexOptions.IgnoreCase).Groups[1].Value;
        var response = await browser.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = TestWebApplicationFactory.AdminUsername,
            ["Input.Password"] = TestWebApplicationFactory.AdminPassword,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token),
        }));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        return browser;
    }
    private static XLWorkbook CreateWorkbook(params string[][] rows)
    {
        var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Sheet1");
        sheet.Cell(1, 1).Value = "测试导入";
        sheet.Cell(2, 1).Value = "用户 ID";
        sheet.Cell(2, 2).Value = "用户名";
        sheet.Cell(2, 3).Value = "班级";
        sheet.Cell(2, 4).Value = "角色";
        sheet.Cell(2, 5).Value = "密码";
        for (var index = 0; index < rows.Length; index++)
        {
            for (var column = 0; column < rows[index].Length; column++)
                sheet.Cell(index + 3, column + 1).Value = rows[index][column];
        }
        return workbook;
    }

    private static MemoryStream Save(XLWorkbook workbook)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }
}