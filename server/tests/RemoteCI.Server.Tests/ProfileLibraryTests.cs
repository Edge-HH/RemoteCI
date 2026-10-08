using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class ProfileLibraryTests
{
    [Fact]
    public void PreviewReportsReferencesAndRejectsOversizeUtf8()
    {
        var preview = ProfileLibraryService.Preview(ProfileTestData.Json());
        Assert.Equal("源档案", preview.Name);
        Assert.Equal(1, preview.TimeLayoutCount);
        Assert.Equal(1, preview.ClassPlanCount);
        Assert.Equal(1, preview.SubjectCount);
        Assert.Empty(preview.Errors);

        var broken = ProfileDocument.Parse(ProfileTestData.Json());
        broken["ClassPlans"]![ProfileTestData.PlanId]!["TimeLayoutId"] = Guid.NewGuid().ToString();
        Assert.NotEmpty(ProfileLibraryService.Preview(broken.ToJsonString()).Errors);
        var tooLargeUtf8 = "{\"附加配置\":\"" + new string('中', ProfileDocument.MaxUtf8Bytes / 3 + 1) + "\"}";
        Assert.True(tooLargeUtf8.Length < ProfileDocument.MaxUtf8Bytes);
        Assert.Throws<ArgumentException>(() => ProfileLibraryService.Preview(tooLargeUtf8));
    }

    [Fact]
    public async Task TemplateAndClassCopiesPreserveNativeIdsAndUnknownFieldsIndependently()
    {
        await using var factory = new TestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var library = scope.ServiceProvider.GetRequiredService<ProfileLibraryService>();
        var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
        var classes = scope.ServiceProvider.GetRequiredService<ClassroomService>();
        var firstClass = await classes.CreateAsync("档案一班");
        var secondClass = await classes.CreateAsync("档案二班");
        var template = (await library.SaveAsync(admin, [ProfileTestData.New("模板")]))[0];
        var copies = await library.SaveAsync(admin,
        [
            ProfileTestData.New("一班副本", firstClass.Id, template.Id),
            ProfileTestData.New("二班副本", secondClass.Id, template.Id),
        ]);

        var classAdmin = await ProfileTestData.CreateMemberAsync(scope.ServiceProvider, "profile.classadmin",
            firstClass.Id, AccountRole.ClassAdministratorId);
        var edited = ProfileDocument.Parse(copies[0].ProfileJson);
        edited["Subjects"]![ProfileTestData.SubjectId]!["TeacherName"] = "本班新教师";
        var saved = (await library.SaveAsync(classAdmin,
            [ProfileTestData.Edit(copies[0], "本班新档案", edited.ToJsonString())], firstClass.Id))[0];

        Assert.Equal(copies[0].Revision + 1, saved.Revision);
        Assert.Equal(template.Id, saved.SourceTemplateId);
        var savedJson = ProfileDocument.Parse(saved.ProfileJson);
        Assert.Equal("本班新教师", savedJson["Subjects"]![ProfileTestData.SubjectId]!["TeacherName"]!.GetValue<string>());
        ProfileTestData.AssertNativeIdsAndAttachments(savedJson);

        // 从数据库重新读取，验证修改班级副本没有触及模板或另一班的完整 JSON。
        db.ChangeTracker.Clear();
        var unchangedTemplate = await library.GetAsync(admin, template.Id);
        var unchangedOther = await library.GetAsync(admin, copies[1].Id);
        Assert.Equal(template.ProfileJson, unchangedTemplate.ProfileJson);
        Assert.Equal(copies[1].ProfileJson, unchangedOther.ProfileJson);
        var renamedTemplate = (await library.SaveAsync(admin, [ProfileTestData.Edit(unchangedTemplate, "模板新版")]))[0];
        Assert.Equal(2, renamedTemplate.Revision);
        Assert.Equal(saved.ProfileJson, (await library.GetAsync(classAdmin, saved.Id, firstClass.Id)).ProfileJson);
    }

    [Fact]
    public async Task InvalidBatchDoesNotSaveAnyItem()
    {
        await using var factory = new TestWebApplicationFactory();
        StoredProfileDto original;
        using (var scope = factory.Services.CreateScope())
        {
            var library = scope.ServiceProvider.GetRequiredService<ProfileLibraryService>();
            var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
            original = (await library.SaveAsync(admin, [ProfileTestData.New("原模板")]))[0];
            var broken = ProfileDocument.Parse(ProfileTestData.Json());
            broken["Subjects"] = new JsonObject();
            await Assert.ThrowsAsync<ArgumentException>(() => library.SaveAsync(admin,
                [ProfileTestData.Edit(original, "不应保存"), ProfileTestData.New("无效模板", json: broken.ToJsonString())]));
        }
        using var verification = factory.Services.CreateScope();
        var rows = await verification.ServiceProvider.GetRequiredService<AppDbContext>().StoredProfiles.AsNoTracking().ToListAsync();
        var persisted = Assert.Single(rows);
        Assert.Equal(original.Name, persisted.Name);
        Assert.Equal(original.Revision, persisted.Revision);
    }

    [Fact]
    public async Task RevisionConflictRollsBackTheWholeBatch()
    {
        await using var factory = new TestWebApplicationFactory();
        IReadOnlyList<StoredProfileDto> originals;
        using (var scope = factory.Services.CreateScope())
        {
            var library = scope.ServiceProvider.GetRequiredService<ProfileLibraryService>();
            var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
            originals = await library.SaveAsync(admin, [ProfileTestData.New("模板一"), ProfileTestData.New("模板二")]);
        }
        using (var otherRequest = factory.Services.CreateScope())
        {
            var library = otherRequest.ServiceProvider.GetRequiredService<ProfileLibraryService>();
            await library.SaveAsync(await ProfileTestData.AdminAsync(otherRequest.ServiceProvider),
                [ProfileTestData.Edit(originals[1], "其他用户的新版本")]);
        }
        using (var staleRequest = factory.Services.CreateScope())
        {
            var library = staleRequest.ServiceProvider.GetRequiredService<ProfileLibraryService>();
            var admin = await ProfileTestData.AdminAsync(staleRequest.ServiceProvider);
            await Assert.ThrowsAsync<ProfileRevisionException>(() => library.SaveAsync(
                admin,
                [ProfileTestData.Edit(originals[0], "不可部分保存"), ProfileTestData.Edit(originals[1], "过期覆盖")]));
        }
        using var verification = factory.Services.CreateScope();
        var rows = await verification.ServiceProvider.GetRequiredService<AppDbContext>().StoredProfiles.AsNoTracking().ToListAsync();
        Assert.Equal("模板一", rows.Single(x => x.Id == originals[0].Id).Name);
        Assert.Equal(originals[0].Revision, rows.Single(x => x.Id == originals[0].Id).Revision);
        Assert.Equal("其他用户的新版本", rows.Single(x => x.Id == originals[1].Id).Name);
        Assert.Equal(originals[1].Revision + 1, rows.Single(x => x.Id == originals[1].Id).Revision);
    }

    [Fact]
    public async Task ClassRoleAndPermissionMustBothAuthorizeTheTargetClass()
    {
        await using var factory = new TestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var library = scope.ServiceProvider.GetRequiredService<ProfileLibraryService>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var classrooms = scope.ServiceProvider.GetRequiredService<ClassroomService>();
        var first = await classrooms.CreateAsync("有权限的班");
        var second = await classrooms.CreateAsync("学生身份的班");
        var classAdmin = await ProfileTestData.CreateMemberAsync(scope.ServiceProvider, "profile.authority", first.Id,
            AccountRole.ClassAdministratorId);
        await classrooms.AddMemberAsync(second.Id, classAdmin.Id, AccountRole.StudentId);
        var teacher = await ProfileTestData.CreateMemberAsync(scope.ServiceProvider, "profile.teacher", first.Id,
            AccountRole.TeacherId, UserPermissions.AccessWebUi | UserPermissions.ManageSchedule);
        var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
        var template = (await library.SaveAsync(admin, [ProfileTestData.New("全局模板")]))[0];
        var own = (await library.SaveAsync(admin, [ProfileTestData.New("本班档案", first.Id)]))[0];

        Assert.True(await library.CanManageClassAsync(classAdmin, first.Id));
        Assert.False(await library.CanManageClassAsync(classAdmin, second.Id));
        Assert.False(await library.CanManageClassAsync(teacher, first.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => library.ListAsync(classAdmin));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => library.GetAsync(classAdmin, template.Id, first.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => library.GetAsync(teacher, own.Id, first.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => library.SaveAsync(classAdmin,
            [ProfileTestData.New("伪造全局模板")], first.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => library.SaveAsync(classAdmin,
            [ProfileTestData.New("其他班档案", second.Id)], first.Id));

        var role = await db.AccountRoles.SingleAsync(x => x.Id == AccountRole.ClassAdministratorId);
        role.DefaultPermissions &= ~UserPermissions.ManageSchedule;
        await db.SaveChangesAsync();
        Assert.False(await library.CanManageClassAsync(classAdmin, first.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => library.GetAsync(classAdmin, own.Id, first.Id));
    }

    [Fact]
    public async Task FailedBatchCannotLeakAnEarlierEditIntoTheNextSaveInTheSameScope()
    {
        await using var factory = new TestWebApplicationFactory();
        IReadOnlyList<StoredProfileDto> originals;
        using (var scope = factory.Services.CreateScope())
        {
            var library = scope.ServiceProvider.GetRequiredService<ProfileLibraryService>();
            var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
            originals = await library.SaveAsync(admin, [ProfileTestData.New("保留一"), ProfileTestData.New("保留二")]);
            var conflict = ProfileTestData.Edit(originals[1]);
            conflict.Revision--;
            await Assert.ThrowsAsync<ProfileRevisionException>(() => library.SaveAsync(admin,
                [ProfileTestData.Edit(originals[0], "失败请求的修改"), conflict]));
            await library.SaveAsync(admin, [ProfileTestData.New("之后的有效请求")]);
        }
        using var verification = factory.Services.CreateScope();
        var persisted = await verification.ServiceProvider.GetRequiredService<AppDbContext>().StoredProfiles.AsNoTracking().ToListAsync();
        Assert.Equal(3, persisted.Count);
        Assert.Equal(originals[0].Name, persisted.Single(x => x.Id == originals[0].Id).Name);
        Assert.Equal(originals[0].Revision, persisted.Single(x => x.Id == originals[0].Id).Revision);
    }

    [Fact]
    public async Task CopyAndDeleteUseExpectedRevisionAndDoNotModifyTheOriginal()
    {
        await using var factory = new TestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var library = scope.ServiceProvider.GetRequiredService<ProfileLibraryService>();
        var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
        var original = (await library.SaveAsync(admin, [ProfileTestData.New("模板")]))[0];
        var copy = await library.CopyAsync(admin, new ProfileIdRequest { Id = original.Id, Revision = original.Revision, Name = "副本" });
        Assert.NotEqual(original.Id, copy.Id);
        ProfileTestData.AssertNativeIdsAndAttachments(ProfileDocument.Parse(copy.ProfileJson));
        await Assert.ThrowsAsync<ProfileRevisionException>(() => library.CopyAsync(admin,
            new ProfileIdRequest { Id = original.Id, Revision = 0 }));
        await Assert.ThrowsAsync<ProfileRevisionException>(() => library.DeleteAsync(admin,
            new ProfileIdRequest { Id = copy.Id, Revision = 0 }));
        await library.DeleteAsync(admin, new ProfileIdRequest { Id = copy.Id, Revision = copy.Revision });
        await Assert.ThrowsAsync<KeyNotFoundException>(() => library.GetAsync(admin, copy.Id));
        Assert.Equal(original.ProfileJson, (await library.GetAsync(admin, original.Id)).ProfileJson);
    }

    [Fact]
    public async Task ClassKeepsOneWorkingProfileAndClassAdministratorCannotForgeItsTemplateSource()
    {
        await using var factory = new TestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var library = scope.ServiceProvider.GetRequiredService<ProfileLibraryService>();
        var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
        var templates = await library.SaveAsync(admin, [ProfileTestData.New("来源一"), ProfileTestData.New("来源二")]);
        var own = (await library.SaveAsync(admin,
            [ProfileTestData.New("唯一班级副本", Classroom.DefaultId, templates[0].Id)]))[0];
        await Assert.ThrowsAsync<ProfileRevisionException>(() => library.SaveAsync(admin,
            [ProfileTestData.New("不允许第二份", Classroom.DefaultId)]));
        var classAdmin = await ProfileTestData.CreateMemberAsync(scope.ServiceProvider, "profile.source.classadmin",
            Classroom.DefaultId, AccountRole.ClassAdministratorId);
        var forged = ProfileTestData.Edit(own, "伪造来源");
        forged.SourceTemplateId = templates[1].Id;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => library.SaveAsync(classAdmin, [forged], Classroom.DefaultId));
        Assert.Equal(templates[0].Id, (await library.GetAsync(classAdmin, own.Id, Classroom.DefaultId)).SourceTemplateId);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Single(await db.StoredProfiles.AsNoTracking().Where(x => x.ClassId == Classroom.DefaultId).ToListAsync());
    }

    [Fact]
    public async Task DeletingTemplatePreservesClassCopyAndInvalidatesItsPreviousRevision()
    {
        await using var factory = new TestWebApplicationFactory();
        StoredProfileDto copy;
        using (var setup = factory.Services.CreateScope())
        {
            var library = setup.ServiceProvider.GetRequiredService<ProfileLibraryService>();
            var admin = await ProfileTestData.AdminAsync(setup.ServiceProvider);
            var template = (await library.SaveAsync(admin, [ProfileTestData.New("可删除模板")]))[0];
            copy = (await library.SaveAsync(admin, [ProfileTestData.New("保留班级副本", Classroom.DefaultId, template.Id)]))[0];
            await library.DeleteAsync(admin, new ProfileIdRequest { Id = template.Id, Revision = template.Revision });
        }
        using var verification = factory.Services.CreateScope();
        var service = verification.ServiceProvider.GetRequiredService<ProfileLibraryService>();
        var classAdmin = await ProfileTestData.CreateMemberAsync(verification.ServiceProvider, "profile.delete.classadmin",
            Classroom.DefaultId, AccountRole.ClassAdministratorId);
        var kept = await service.GetAsync(classAdmin, copy.Id, Classroom.DefaultId);
        Assert.Equal(copy.ProfileJson, kept.ProfileJson);
        Assert.Equal(copy.Name, kept.Name);
        Assert.Null(kept.SourceTemplateId);
        Assert.Equal(copy.Revision + 1, kept.Revision);
        await Assert.ThrowsAsync<ProfileRevisionException>(() => service.SaveAsync(classAdmin,
            [ProfileTestData.Edit(copy, "旧页面覆盖")], Classroom.DefaultId));
    }

    [Fact]
    public async Task ProfilesPersistAcrossServiceRestartOnTheSameDatabase()
    {
        StoredProfileDto template;
        StoredProfileDto own;
        string path;
        await using (var first = new TestWebApplicationFactory())
        {
            path = first.DatabasePath;
            using var scope = first.Services.CreateScope();
            var library = scope.ServiceProvider.GetRequiredService<ProfileLibraryService>();
            var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
            template = (await library.SaveAsync(admin, [ProfileTestData.New("持久模板")]))[0];
            own = (await library.SaveAsync(admin, [ProfileTestData.New("持久副本", Classroom.DefaultId, template.Id)]))[0];
            own = (await library.SaveAsync(admin, [ProfileTestData.Edit(own, "持久副本修订")]))[0];
        }
        await using var restarted = TestWebApplicationFactory.ForDatabase(path);
        using var verification = restarted.Services.CreateScope();
        var service = verification.ServiceProvider.GetRequiredService<ProfileLibraryService>();
        var actor = await ProfileTestData.AdminAsync(verification.ServiceProvider);
        Assert.Equal(template, await service.GetAsync(actor, template.Id));
        Assert.Equal(own, await service.GetAsync(actor, own.Id));
    }

    [Fact]
    public async Task BackupSchemaFiveRestoresProfilesAndLegacyBackupRestoresAnEmptyLibrary()
    {
        await using var factory = new TestWebApplicationFactory();
        ConfigurationSnapshot snapshot;
        IReadOnlyList<StoredProfileDto> expected;
        using (var source = factory.Services.CreateScope())
        {
            var library = source.ServiceProvider.GetRequiredService<ProfileLibraryService>();
            var actor = await ProfileTestData.AdminAsync(source.ServiceProvider);
            var template = (await library.SaveAsync(actor, [ProfileTestData.New("备份模板")]))[0];
            var own = (await library.SaveAsync(actor, [ProfileTestData.New("备份班级", Classroom.DefaultId, template.Id)]))[0];
            expected = [template, own];
            snapshot = await source.ServiceProvider.GetRequiredService<ConfigurationArchiveService>().CaptureAsync();
            Assert.Equal(5, snapshot.Version);
            var serialized = JsonSerializer.SerializeToNode(snapshot, JsonDefaults.Options)!;
            Assert.Equal(2, serialized["profiles"]!.AsArray().Count);
        }
        using (var restore = factory.Services.CreateScope())
        {
            await restore.ServiceProvider.GetRequiredService<ConfigurationArchiveService>().ApplyAsync(snapshot);
            var library = restore.ServiceProvider.GetRequiredService<ProfileLibraryService>();
            var actor = await ProfileTestData.AdminAsync(restore.ServiceProvider);
            foreach (var profile in expected) Assert.Equal(profile, await library.GetAsync(actor, profile.Id));
        }
        // 通过真实 JSON 缺字段的旧包验证兼容，而非只把当前 schema 的列表清空。
        var legacyJson = JsonSerializer.SerializeToNode(snapshot, JsonDefaults.Options)!.AsObject();
        legacyJson.Remove("profiles");
        legacyJson["version"] = 4;
        var legacy = legacyJson.Deserialize<ConfigurationSnapshot>(JsonDefaults.Options)!;
        using var oldRestore = factory.Services.CreateScope();
        await oldRestore.ServiceProvider.GetRequiredService<ConfigurationArchiveService>().ApplyAsync(legacy);
        Assert.Empty(await oldRestore.ServiceProvider.GetRequiredService<AppDbContext>().StoredProfiles.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task DispatchRequiresExplicitModeConfirmationAndSavedRevisionAndDoesNotQueueOffline()
    {
        await using var factory = new TestWebApplicationFactory();
        using var scope = factory.Services.CreateScope();
        var admin = await ProfileTestData.AdminAsync(scope.ServiceProvider);
        var profile = (await scope.ServiceProvider.GetRequiredService<ProfileLibraryService>().SaveAsync(admin,
            [ProfileTestData.New("下发档案", Classroom.DefaultId)]))[0];
        var dispatch = scope.ServiceProvider.GetRequiredService<ProfileDispatchService>();
        var request = ProfileTestData.Dispatch(profile);
        request.Mode = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => dispatch.ApplyAsync(admin, request));
        request.Mode = ProfileApplyMode.ReplaceSections;
        await Assert.ThrowsAsync<ArgumentException>(() => dispatch.ApplyAsync(admin, request));
        request.Mode = ProfileApplyMode.CreateAndActivate;
        await Assert.ThrowsAsync<ArgumentException>(() => dispatch.ApplyAsync(admin, request));
        request.Mode = ProfileApplyMode.MergeCurrent;
        request.Items[0].Revision--;
        await Assert.ThrowsAsync<ProfileRevisionException>(() => dispatch.ApplyAsync(admin, request));
        request.Items[0].Revision = profile.Revision;
        var failed = Assert.Single(await dispatch.ApplyAsync(admin, request));
        Assert.False(failed.Success);
        Assert.Equal(Classroom.DefaultId, failed.ClassId);
        Assert.Contains("未在线", failed.Message);
        Assert.Equal(profile, await scope.ServiceProvider.GetRequiredService<ProfileLibraryService>().GetAsync(admin, profile.Id));
    }
}

/// <summary>使用原生对象 ID 与未知字段的真实档案结构，供持久化和页面集成测试共用。</summary>
internal static class ProfileTestData
{
    public const string Password = "Profile-Test-Password-2026";
    public const string LayoutId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    public const string PlanId = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    public const string SubjectId = "cccccccc-cccc-cccc-cccc-cccccccccccc";

    public static string Json(string name = "源档案") => new JsonObject
    {
        ["Name"] = name,
        ["PluginAttachment"] = new JsonObject { ["enabled"] = true, ["vendor"] = "keep-root" },
        ["TimeLayouts"] = new JsonObject
        {
            [LayoutId] = new JsonObject
            {
                ["Name"] = "常规时间表", ["ExtraInfo"] = new JsonObject { ["vendor.option"] = "keep-layout" },
                ["Layouts"] = new JsonArray(new JsonObject
                {
                    ["StartTime"] = "08:00:00", ["EndTime"] = "08:40:00", ["TimeType"] = 0,
                    ["DefaultClassId"] = SubjectId, ["FuturePointOption"] = "keep-point",
                }),
            },
        },
        ["ClassPlans"] = new JsonObject
        {
            [PlanId] = new JsonObject
            {
                ["Name"] = "周一课表", ["TimeLayoutId"] = LayoutId, ["IsEnabled"] = true,
                ["TimeRule"] = new JsonObject { ["WeekDay"] = 1, ["WeekCountDiv"] = 0, ["WeekCountDivTotal"] = 2 },
                ["Classes"] = new JsonArray(new JsonObject { ["SubjectId"] = SubjectId, ["FutureLessonOption"] = "keep-course" }),
            },
        },
        ["Subjects"] = new JsonObject
        {
            [SubjectId] = new JsonObject
            {
                ["Name"] = "语文", ["Initials"] = "语", ["TeacherName"] = "原教师", ["FutureSubjectOption"] = "keep-subject",
            },
        },
    }.ToJsonString();

    public static ProfileSaveItem New(string name, Guid? classId = null, Guid? templateId = null, string? json = null) => new()
    {
        Name = name, ClassId = classId, SourceTemplateId = templateId, ProfileJson = json ?? Json(),
    };

    public static ProfileSaveItem Edit(StoredProfileDto source, string? name = null, string? json = null) => new()
    {
        Id = source.Id, ClassId = source.ClassId, SourceTemplateId = source.SourceTemplateId,
        Name = name ?? source.Name, Revision = source.Revision, ProfileJson = json ?? source.ProfileJson,
    };

    public static ProfileDispatchRequest Dispatch(StoredProfileDto source) => new()
    {
        Items = [new ProfileIdRequest { Id = source.Id, Revision = source.Revision }],
        ClassIds = [source.ClassId ?? Classroom.DefaultId], Mode = ProfileApplyMode.MergeCurrent,
        Sections = ProfileDistributionSection.TimeLayouts | ProfileDistributionSection.ClassPlans | ProfileDistributionSection.Subjects,
    };

    public static async Task<AppUser> AdminAsync(IServiceProvider services) => await services.GetRequiredService<AppDbContext>()
        .Users.SingleAsync(x => x.UserName == TestWebApplicationFactory.AdminUsername);

    public static async Task<AppUser> CreateMemberAsync(IServiceProvider services, string name, Guid classId,
        Guid roleId, UserPermissions granted = UserPermissions.None, Guid? globalRoleId = null)
    {
        var user = await services.GetRequiredService<IdentityCoordinator>().CreateUserAsync(new CreateUserRequest
        {
            Username = name, DisplayName = name, Password = Password, RoleId = globalRoleId ?? roleId, GrantedPermissions = granted,
        });
        await services.GetRequiredService<ClassroomService>().AddMemberAsync(classId, user.Id, roleId);
        return await services.GetRequiredService<AppDbContext>().Users.SingleAsync(x => x.Id == user.Id);
    }

    public static void AssertNativeIdsAndAttachments(JsonObject json)
    {
        Assert.Equal("keep-root", json["PluginAttachment"]!["vendor"]!.GetValue<string>());
        Assert.Equal("keep-layout", json["TimeLayouts"]![LayoutId]!["ExtraInfo"]!["vendor.option"]!.GetValue<string>());
        Assert.Equal("keep-point", json["TimeLayouts"]![LayoutId]!["Layouts"]![0]!["FuturePointOption"]!.GetValue<string>());
        Assert.Equal("keep-course", json["ClassPlans"]![PlanId]!["Classes"]![0]!["FutureLessonOption"]!.GetValue<string>());
        Assert.Equal("keep-subject", json["Subjects"]![SubjectId]!["FutureSubjectOption"]!.GetValue<string>());
        Assert.Equal(LayoutId, json["ClassPlans"]![PlanId]!["TimeLayoutId"]!.GetValue<string>());
        Assert.Equal(SubjectId, json["ClassPlans"]![PlanId]!["Classes"]![0]!["SubjectId"]!.GetValue<string>());
    }
}
