using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassIsland.Shared.Models.Profile;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ProfileApplyTests
{
    private static readonly Guid LayoutId = Guid.Parse("aa111111-1111-1111-1111-111111111111");
    private static readonly Guid PlanId = Guid.Parse("bb111111-1111-1111-1111-111111111111");
    private static readonly Guid SubjectId = Guid.Parse("cc111111-1111-1111-1111-111111111111");
    private static readonly Guid OtherSubjectId = Guid.Parse("dd111111-1111-1111-1111-111111111111");

    private static string ProfileJson(string teacher = "新老师", bool withExtraSubject = false)
    {
        var profile = new JsonObject
        {
            ["Name"] = "示例档案",
            ["FutureRootField"] = new JsonObject { ["Enabled"] = true },
            ["TimeLayouts"] = new JsonObject
            {
                [LayoutId.ToString()] = new JsonObject
                {
                    ["Name"] = "工作日",
                    ["Layouts"] = new JsonArray(new JsonObject
                    {
                        ["StartTime"] = "08:00:00", ["EndTime"] = "08:45:00", ["TimeType"] = 0,
                        ["DefaultClassId"] = SubjectId.ToString(),
                        ["AttachedObjects"] = new JsonObject { [Guid.NewGuid().ToString()] = new JsonObject { ["Value"] = 7 } },
                    }),
                },
            },
            ["ClassPlans"] = new JsonObject
            {
                [PlanId.ToString()] = new JsonObject
                {
                    ["Name"] = "周一", ["TimeLayoutId"] = LayoutId.ToString(), ["IsEnabled"] = true,
                    ["TimeRule"] = new JsonObject { ["WeekDay"] = 1, ["WeekCountDiv"] = 0, ["WeekCountDivTotal"] = 2 },
                    ["Classes"] = new JsonArray(new JsonObject { ["SubjectId"] = SubjectId.ToString() }),
                },
            },
            ["Subjects"] = new JsonObject
            {
                [SubjectId.ToString()] = new JsonObject
                {
                    ["Name"] = "语文", ["Initial"] = "语", ["TeacherName"] = teacher,
                    ["FutureSubjectField"] = new JsonArray(1, 2, 3),
                },
            },
        };
        if (withExtraSubject) profile["Subjects"]![OtherSubjectId.ToString()] = new JsonObject { ["Name"] = "体育" };
        return profile.ToJsonString();
    }

    [Fact]
    public void RoundTripPreservesUnknownAndAttachedFields()
    {
        var json = ProfileJson();
        var parsed = ProfileDocument.Parse(json);
        Assert.Empty(ProfileDocument.Validate(parsed));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(parsed.ToJsonString())));
    }

    [Fact]
    public void ActualClassIslandSdkSerializationIsAccepted()
    {
        var profile = new Profile();
        profile.Subjects[SubjectId] = new Subject { Name = "语文" };
        profile.TimeLayouts[LayoutId] = new TimeLayout();
        profile.TimeLayouts[LayoutId].Layouts.Add(new TimeLayoutItem
        {
            StartTime = TimeSpan.FromHours(8), EndTime = TimeSpan.FromMinutes(525), TimeType = 0, DefaultClassId = SubjectId,
        });
        profile.ClassPlans[PlanId] = new ClassPlan { TimeLayoutId = LayoutId };
        profile.ClassPlans[PlanId].Classes.Add(new ClassInfo { SubjectId = SubjectId });
        Assert.Empty(ProfileDocument.Validate(JsonSerializer.Serialize(profile)));
    }

    [Fact]
    public void SizeLimitUsesUtf8BytesRatherThanCharacters()
    {
        var json = "{\"Comment\":\"" + new string('汉', ProfileDocument.MaxUtf8Bytes / 3 + 1) + "\"}";
        Assert.True(json.Length < ProfileDocument.MaxUtf8Bytes);
        Assert.True(Encoding.UTF8.GetByteCount(json) > ProfileDocument.MaxUtf8Bytes);
        Assert.Contains("5 MB", Assert.Single(ProfileDocument.Validate(json)));
    }

    [Fact]
    public void ValidChineseUploadDoesNotGrowBeyondLimitDuringSelection()
    {
        var profile = ProfileDocument.Parse(ProfileJson());
        profile["LargeUnknownMetadata"] = new string('汉', ProfileDocument.MaxUtf8Bytes / 3 - 1024);
        var json = ProfileDocument.Serialize(profile);
        Assert.True(Encoding.UTF8.GetByteCount(json) < ProfileDocument.MaxUtf8Bytes);
        var selected = ProfileDocument.BuildSelection(json, ProfileDistributionSection.ClassPlans);
        Assert.True(Encoding.UTF8.GetByteCount(selected) < ProfileDocument.MaxUtf8Bytes);
        Assert.Empty(ProfileDocument.Validate(selected));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("class")]
    public void TimeTypeMustBeAnInteger(string value)
    {
        var profile = ProfileDocument.Parse(ProfileJson());
        profile["TimeLayouts"]![LayoutId.ToString()]!["Layouts"]![0]!["TimeType"] = value;
        Assert.Contains(ProfileDocument.Validate(profile), error => error.Contains("类型必须"));
    }

    [Fact]
    public void PlanSelectionIncludesDependenciesWithoutExtraSubjects()
    {
        var selected = ProfileDocument.Parse(ProfileDocument.BuildSelection(ProfileJson(withExtraSubject: true),
            ProfileDistributionSection.ClassPlans, classPlanIds: [PlanId]));
        Assert.Single(selected["ClassPlans"]!.AsObject());
        Assert.Single(selected["TimeLayouts"]!.AsObject());
        Assert.Single(selected["Subjects"]!.AsObject());
        Assert.NotNull(selected["FutureRootField"]);
        Assert.NotNull(selected["Subjects"]![SubjectId.ToString()]!["FutureSubjectField"]);
    }

    [Fact]
    public void LayoutSelectionIncludesDefaultSubject()
    {
        var selected = ProfileDocument.Parse(ProfileDocument.BuildSelection(ProfileJson(), ProfileDistributionSection.TimeLayouts));
        Assert.Empty(selected["ClassPlans"]!.AsObject());
        Assert.Single(selected["Subjects"]!.AsObject());
        Assert.Empty(ProfileDocument.Validate(selected));
    }

    [Fact]
    public void MergeUpdatesDependencyAndPreservesUnselectedObjects()
    {
        var applied = ProfileDocument.Parse(ProfileDocument.Apply(ProfileJson("原老师", true), Request(ProfileApplyMode.MergeCurrent)));
        Assert.Equal("新老师", applied["Subjects"]![SubjectId.ToString()]!["TeacherName"]!.GetValue<string>());
        Assert.NotNull(applied["Subjects"]![OtherSubjectId.ToString()]);
        Assert.NotNull(applied["FutureRootField"]);
    }

    [Fact]
    public void ReplaceOnlyClearsExplicitCategoriesAndMergesDependencies()
    {
        var applied = ProfileDocument.Parse(ProfileDocument.Apply(ProfileJson("原老师", true), Request(ProfileApplyMode.ReplaceSections)));
        Assert.NotNull(applied["Subjects"]![OtherSubjectId.ToString()]);
        Assert.Equal("新老师", applied["Subjects"]![SubjectId.ToString()]!["TeacherName"]!.GetValue<string>());
    }

    [Fact]
    public void ReplacementCannotLeaveExistingPlanReferencesDangling()
    {
        var source = ProfileDocument.Parse(ProfileJson());
        source["ClassPlans"] = new JsonObject();
        source["TimeLayouts"] = new JsonObject();
        var request = new ProfileApplyRequest
        {
            Mode = ProfileApplyMode.ReplaceSections, Sections = ProfileDistributionSection.TimeLayouts, ProfileJson = source.ToJsonString(),
        };
        var error = Assert.Throws<ArgumentException>(() => ProfileDocument.Apply(ProfileJson(), request));
        Assert.Contains("不存在", error.Message);
    }

    private static readonly Guid OverlayId = Guid.Parse("ee111111-1111-1111-1111-111111111111");

    /// <summary>RemoteCI 换课或 ClassIsland 临时课表会留下的设备状态：临时层课表、预定课表与临时课表指针。</summary>
    private static string ProfileWithTemporaryPlans(bool withExtraSubject = false)
    {
        var current = ProfileDocument.Parse(ProfileJson("原老师", withExtraSubject));
        var overlay = current["ClassPlans"]![PlanId.ToString()]!.DeepClone().AsObject();
        overlay["IsOverlay"] = true;
        overlay["OverlaySourceId"] = PlanId.ToString();
        current["ClassPlans"]![OverlayId.ToString()] = overlay;
        current["OrderedSchedules"] = new JsonObject
        {
            ["2026-10-10T00:00:00"] = new JsonObject { ["ClassPlanId"] = OverlayId.ToString() },
            ["2026-10-12T00:00:00"] = new JsonObject { ["ClassPlanId"] = PlanId.ToString() },
        };
        current["TempClassPlanId"] = OverlayId.ToString();
        return current.ToJsonString();
    }

    [Fact]
    public void ReplacingClassPlansKeepsTemporaryLayersWhoseDependenciesRemain()
    {
        // 临时层不属于常规“课表”类别：整体替换常规课表后，依赖仍有效的设备临时层保留。
        var applied = ProfileDocument.Parse(ProfileDocument.Apply(ProfileWithTemporaryPlans(), Request(ProfileApplyMode.ReplaceSections)));

        Assert.NotNull(applied["ClassPlans"]![OverlayId.ToString()]);
        var ordered = applied["OrderedSchedules"]!.AsObject();
        Assert.True(ordered.ContainsKey("2026-10-10T00:00:00"));
        Assert.True(ordered.ContainsKey("2026-10-12T00:00:00"));
        Assert.Equal(OverlayId.ToString(), applied["TempClassPlanId"]!.GetValue<string>());
    }

    [Fact]
    public void ReplacingTimeLayoutsAlignsTemporaryLayersLikeTheHost()
    {
        // 新时间表多了一节课：与宿主 RefreshClassesList 一致，设备上的临时层补一节空课，老师的换课保留。
        var source = ProfileDocument.Parse(ProfileJson());
        source["TimeLayouts"]![LayoutId.ToString()]!["Layouts"]!.AsArray().Add(new JsonObject
        {
            ["StartTime"] = "09:00:00", ["EndTime"] = "09:45:00", ["TimeType"] = 0,
        });
        source["ClassPlans"]![PlanId.ToString()]!["Classes"]!.AsArray().Add(new JsonObject { ["SubjectId"] = SubjectId.ToString() });
        var request = new ProfileApplyRequest
        {
            Mode = ProfileApplyMode.ReplaceSections, ProfileJson = source.ToJsonString(),
            Sections = ProfileDistributionSection.TimeLayouts | ProfileDistributionSection.ClassPlans,
        };

        var applied = ProfileDocument.Parse(ProfileDocument.Apply(ProfileWithTemporaryPlans(), request));

        var classes = applied["ClassPlans"]![OverlayId.ToString()]!["Classes"]!.AsArray();
        Assert.Equal(2, classes.Count);
        Assert.Equal(SubjectId.ToString(), classes[0]!["SubjectId"]!.GetValue<string>());
        Assert.Equal(Guid.Empty.ToString(), classes[1]!["SubjectId"]!.GetValue<string>());
        Assert.True(applied["OrderedSchedules"]!.AsObject().ContainsKey("2026-10-10T00:00:00"));
        Assert.Empty(ProfileDocument.Validate(applied));
    }

    [Fact]
    public void TemporaryLayerWhoseTimeLayoutDisappearsIsRemoved()
    {
        // 整体替换时间表后原时间表不复存在，临时层无法显示，连同日期与指针一起移除；缺失科目的临时层只清空该节。
        var source = ProfileDocument.Parse(ProfileJson());
        var newLayout = Guid.NewGuid().ToString();
        source["TimeLayouts"] = new JsonObject { [newLayout] = source["TimeLayouts"]![LayoutId.ToString()]!.DeepClone() };
        source["ClassPlans"]![PlanId.ToString()]!["TimeLayoutId"] = newLayout;
        var request = new ProfileApplyRequest
        {
            Mode = ProfileApplyMode.ReplaceSections, ProfileJson = source.ToJsonString(),
            Sections = ProfileDistributionSection.TimeLayouts | ProfileDistributionSection.ClassPlans,
        };

        var applied = ProfileDocument.Parse(ProfileDocument.Apply(ProfileWithTemporaryPlans(), request));

        Assert.Null(applied["ClassPlans"]![OverlayId.ToString()]);
        Assert.False(applied["OrderedSchedules"]!.AsObject().ContainsKey("2026-10-10T00:00:00"));
        Assert.Equal(Guid.Empty.ToString(), applied["TempClassPlanId"]!.GetValue<string>());
        Assert.Empty(ProfileDocument.Validate(applied));

        var device = ProfileDocument.Parse(ProfileWithTemporaryPlans(withExtraSubject: true));
        device["ClassPlans"]![OverlayId.ToString()]!["Classes"]![0]!["SubjectId"] = OtherSubjectId.ToString();
        var subjectsOnly = new ProfileApplyRequest
        {
            Mode = ProfileApplyMode.ReplaceSections, ProfileJson = ProfileJson(), Sections = ProfileDistributionSection.Subjects,
        };
        var repaired = ProfileDocument.Parse(ProfileDocument.Apply(ProfileDocument.Serialize(device), subjectsOnly));
        Assert.Equal(Guid.Empty.ToString(), repaired["ClassPlans"]![OverlayId.ToString()]!["Classes"]![0]!["SubjectId"]!.GetValue<string>());
    }

    [Fact]
    public void DanglingDeviceReferencesDoNotBlockMerge()
    {
        var current = ProfileDocument.Parse(ProfileJson("原老师"));
        current["OrderedSchedules"] = new JsonObject
        {
            ["2026-10-10T00:00:00"] = new JsonObject { ["ClassPlanId"] = Guid.NewGuid().ToString() },
        };

        var applied = ProfileDocument.Parse(ProfileDocument.Apply(current.ToJsonString(), Request(ProfileApplyMode.MergeCurrent)));

        Assert.Empty(applied["OrderedSchedules"]!.AsObject());
    }

    [Fact]
    public void HostAdapterReplaceKeepsValidHostTemporaryLayers()
    {
        using var host = new FakeHost(ProfileWithTemporaryPlans());

        var result = ProfileApplyExecutor.Apply(Request(ProfileApplyMode.ReplaceSections), new ClassIslandProfileApplicationBackend(host));

        Assert.True(result.Success, result.Message);
        Assert.Contains(new DateTime(2026, 10, 10), host.Profile.OrderedSchedules.Keys);
        Assert.True(host.Profile.ClassPlans[OverlayId].IsOverlay);
        Assert.Contains(new DateTime(2026, 10, 12), host.Profile.OrderedSchedules.Keys);
        // 宿主保存后的档案必须仍能通过校验，否则之后的每次下发都会被拒绝。
        Assert.Empty(ProfileDocument.Validate(JsonSerializer.Serialize(host.Profile)));
    }

    [Theory]
    [InlineData("TimeLayoutId")]
    [InlineData("SubjectId")]
    [InlineData("DefaultClassId")]
    public void MissingReferencesAreRejected(string field)
    {
        var profile = ProfileDocument.Parse(ProfileJson());
        JsonNode node = field switch
        {
            "TimeLayoutId" => profile["ClassPlans"]![PlanId.ToString()]!,
            "SubjectId" => profile["ClassPlans"]![PlanId.ToString()]!["Classes"]![0]!,
            _ => profile["TimeLayouts"]![LayoutId.ToString()]!["Layouts"]![0]!,
        };
        node[field] = Guid.NewGuid().ToString();
        Assert.Contains(ProfileDocument.Validate(profile), error => error.Contains("不存在"));
    }

    [Fact]
    public void InvalidTimeAndScheduleLengthAreRejected()
    {
        var profile = ProfileDocument.Parse(ProfileJson());
        profile["TimeLayouts"]![LayoutId.ToString()]!["Layouts"]![0]!["EndTime"] = "07:00:00";
        profile["ClassPlans"]![PlanId.ToString()]!["Classes"] = new JsonArray();
        var errors = ProfileDocument.Validate(profile);
        Assert.Contains(errors, error => error.Contains("时间无效"));
        Assert.Contains(errors, error => error.Contains("数量不一致"));
    }

    [Fact]
    public void EmptySubjectIsAllowedButMalformedReferenceIsRejected()
    {
        var profile = ProfileDocument.Parse(ProfileJson());
        var lesson = profile["ClassPlans"]![PlanId.ToString()]!["Classes"]![0]!;
        lesson["SubjectId"] = Guid.Empty.ToString();
        Assert.Empty(ProfileDocument.Validate(profile));
        lesson["SubjectId"] = 123;
        Assert.NotEmpty(ProfileDocument.Validate(profile));
    }

    [Theory]
    [InlineData("Name", false)]
    [InlineData("TeacherName", false)]
    [InlineData("IsEnabled", true)]
    public void KnownFieldsCannotUseUnexpectedTypes(string field, bool booleanField)
    {
        var profile = ProfileDocument.Parse(ProfileJson());
        var node = field switch
        {
            "TeacherName" => profile["Subjects"]![SubjectId.ToString()]!,
            "IsEnabled" => profile["ClassPlans"]![PlanId.ToString()]!,
            _ => profile,
        };
        node[field] = booleanField ? JsonValue.Create("true") : JsonValue.Create(123);
        Assert.Contains(ProfileDocument.Validate(profile), error => error.Contains(field));
        node[field] = null;
        Assert.Contains(ProfileDocument.Validate(profile), error => error.Contains(field));
    }

    [Fact]
    public void EmptyGuidGroupIsSupportedButMalformedProfileIdIsRejected()
    {
        var profile = ProfileDocument.Parse(ProfileJson());
        profile["ClassPlanGroups"] = new JsonObject { [Guid.Empty.ToString()] = new JsonObject { ["Name"] = "全局课表群", ["IsGlobal"] = true } };
        Assert.Empty(ProfileDocument.Validate(profile));
        profile["Id"] = 123;
        Assert.Contains(ProfileDocument.Validate(profile), error => error.Contains("GUID"));
    }

    [Fact]
    public void CaseInsensitiveDuplicateGuidsAndKnownFieldsAreRejected()
    {
        var profile = ProfileDocument.Parse(ProfileJson());
        profile["Subjects"]!.AsObject()[SubjectId.ToString().ToUpperInvariant()] = new JsonObject { ["Name"] = "重复科目" };
        Assert.Contains(ProfileDocument.Validate(profile.ToJsonString()), error => error.Contains("重复对象"));
        profile = ProfileDocument.Parse(ProfileJson());
        profile["name"] = "另一个名称";
        Assert.Contains(ProfileDocument.Validate(profile), error => error.Contains("重复定义"));
        Assert.Throws<ArgumentException>(() => ProfileDocument.Parse("{\"Name\":\"甲\",\"Name\":\"乙\"}"));
    }

    [Fact]
    public void UnknownFieldsCanHaveDistinctCaseSensitiveKeys()
    {
        var profile = ProfileDocument.Parse(ProfileJson());
        profile["Metadata"] = new JsonObject { ["Token"] = 1, ["token"] = 2 };
        var parsed = ProfileDocument.Parse(profile.ToJsonString());
        Assert.Empty(ProfileDocument.Validate(parsed));
        Assert.Equal(2, parsed["Metadata"]!.AsObject().Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OverlayCyclesAreRejectedForLayoutsAndPlans(bool timeLayout)
    {
        var profile = ProfileDocument.Parse(ProfileJson());
        var originalId = timeLayout ? LayoutId : PlanId;
        var secondId = Guid.NewGuid();
        var dictionary = profile[timeLayout ? "TimeLayouts" : "ClassPlans"]!.AsObject();
        var original = dictionary[originalId.ToString()]!.AsObject();
        var second = original.DeepClone().AsObject();
        original["OverlaySourceId"] = secondId.ToString();
        second["OverlaySourceId"] = originalId.ToString();
        dictionary[secondId.ToString()] = second;
        Assert.Contains(ProfileDocument.Validate(profile), error => error.Contains("循环"));
    }

    [Fact]
    public void FailedSaveRestoresCurrentState()
    {
        var original = ProfileJson("原老师", true);
        var backend = new FakeBackend(original) { FailSave = true };
        var result = ProfileApplyExecutor.Apply(Request(ProfileApplyMode.MergeCurrent), backend);
        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.SaveFailed, result.Code);
        Assert.Equal(original, backend.Json);
        Assert.Equal("原档案", backend.ActiveName);
        Assert.Equal(1, backend.RestoreCount);
    }

    [Fact]
    public void CreateAndActivateUsesExplicitNameAndHasCompleteDependencies()
    {
        var backend = new FakeBackend(ProfileJson("原老师", true));
        var result = ProfileApplyExecutor.Apply(Request(ProfileApplyMode.CreateAndActivate), backend);
        Assert.True(result.Success);
        Assert.Equal("新档案", backend.ActiveName);
        Assert.Empty(ProfileDocument.Validate(backend.Json));
        Assert.Single(ProfileDocument.Parse(backend.Json)["Subjects"]!.AsObject());
    }

    [Fact]
    public void CreateSaveFailureRestoresActiveProfileAndDeletesNewFile()
    {
        var original = ProfileJson("原老师");
        var backend = new FakeBackend(original) { FailSave = true };
        Assert.False(ProfileApplyExecutor.Apply(Request(ProfileApplyMode.CreateAndActivate), backend).Success);
        Assert.Equal(original, backend.Json);
        Assert.Equal("原档案", backend.ActiveName);
        Assert.Empty(backend.Files);
    }

    [Fact]
    public void ExistingNameIsRejectedBeforeAnyMutation()
    {
        var backend = new FakeBackend(ProfileJson());
        backend.Files.Add("新档案");
        Assert.False(ProfileApplyExecutor.Apply(Request(ProfileApplyMode.CreateAndActivate), backend).Success);
        Assert.Equal(0, backend.CaptureCount);
        Assert.Equal("原档案", backend.ActiveName);
    }

    [Fact]
    public void ModeMustBeChosenAndInvalidInputDoesNotMutate()
    {
        var backend = new FakeBackend(ProfileJson());
        var result = ProfileApplyExecutor.Apply(Request((ProfileApplyMode)0), backend);
        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.InvalidRequest, result.Code);
        Assert.Equal(0, backend.CaptureCount);
    }

    [Theory]
    [InlineData("../恶意")]
    [InlineData("C:\\档案")]
    [InlineData("CON")]
    [InlineData("档案.")]
    [InlineData("")]
    public void InvalidImportNameCannotBecomeFilePath(string name) =>
        Assert.Throws<ArgumentException>(() => ProfileApplyExecutor.NormalizeImportName(name));

    [Fact]
    public void CapabilityIsNotAssumedForOldPeersAndLanRejectsCommand()
    {
        Assert.DoesNotContain(RemoteCiCapabilities.ProfileApply, RemoteCiCapabilities.Baseline);
        Assert.Contains(RemoteCiCapabilities.ProfileApply, RemoteCiCapabilities.Current);
        Assert.Equal(UserPermissions.ManageSchedule, CommandPermissions.Required(CommandKind.ApplyProfile));
        Assert.True(CommandPermissions.IsServerOnly(CommandKind.ApplyProfile));
        foreach (var capability in new[] { RemoteCiCapabilities.ProfileRead, RemoteCiCapabilities.ProfileTempLayer })
        {
            Assert.DoesNotContain(capability, RemoteCiCapabilities.Baseline);
            Assert.Contains(capability, RemoteCiCapabilities.Current);
        }
        Assert.Equal(RemoteCiCapabilities.ProfileRead, RemoteCiCapabilities.Required(CommandKind.ReadProfile));
        Assert.Equal(UserPermissions.ManageSchedule, CommandPermissions.Required(CommandKind.ReadProfile));
        Assert.True(CommandPermissions.IsServerOnly(CommandKind.ReadProfile));
    }

    [Fact]
    public void HostAdapterPreservesCurrentRootIdentityAndWritesHostProfileFile()
    {
        using var host = new FakeHost(ProfileJson("原老师", true));
        var root = host.Profile;
        var result = ProfileApplyExecutor.Apply(Request(ProfileApplyMode.MergeCurrent), new ClassIslandProfileApplicationBackend(host));
        Assert.True(result.Success, result.Message);
        Assert.Same(root, host.Profile);
        Assert.Equal("新老师", host.Profile.Subjects[SubjectId].TeacherName);
        Assert.True(File.Exists(Path.Combine(FakeHost.ProfilePath, "原档案.json")));
    }

    [Fact]
    public void HostAdapterRestoresMemoryAndExactFileBytesWhenSaveThrows()
    {
        using var host = new FakeHost(ProfileJson("原老师", true)) { FailSave = true };
        var root = host.Profile;
        var original = File.ReadAllBytes(Path.Combine(FakeHost.ProfilePath, "原档案.json"));
        var result = ProfileApplyExecutor.Apply(Request(ProfileApplyMode.MergeCurrent), new ClassIslandProfileApplicationBackend(host));
        Assert.False(result.Success);
        Assert.Same(root, host.Profile);
        Assert.Equal("原老师", host.Profile.Subjects[SubjectId].TeacherName);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(FakeHost.ProfilePath, "原档案.json")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void HostAdapterMigratesLegacySecondsBeforeApplying(bool numeric)
    {
        using var host = new FakeHost(ProfileJson("原老师"));
        var source = ProfileDocument.Parse(ProfileJson());
        var point = source["TimeLayouts"]![LayoutId.ToString()]!["Layouts"]![0]!.AsObject();
        point.Remove("StartTime");
        point.Remove("EndTime");
        point["StartSecond"] = numeric ? JsonValue.Create(28800) : JsonValue.Create("28800");
        point["EndSecond"] = numeric ? JsonValue.Create(31500) : JsonValue.Create("31500");
        var request = Request(ProfileApplyMode.MergeCurrent);
        request.ProfileJson = ProfileDocument.Serialize(source);
        var result = ProfileApplyExecutor.Apply(request, new ClassIslandProfileApplicationBackend(host));
        Assert.True(result.Success, result.Message);
        Assert.Equal(TimeSpan.FromHours(8), host.Profile.TimeLayouts[LayoutId].Layouts[0].StartTime);
        Assert.Equal(TimeSpan.FromMinutes(525), host.Profile.TimeLayouts[LayoutId].Layouts[0].EndTime);
    }

    [Theory]
    [InlineData("2024-01-01T08:00:00+08:00", "2024-01-01T08:45:00+08:00")]
    [InlineData("2024-01-01T08:00:00", "2024-01-01T08:45:00")]
    [InlineData("08:00:00", "08:45:00")]
    public void HostAdapterMigratesLegacyClockTimesWithoutTimezoneConversion(string start, string end)
    {
        using var host = new FakeHost(ProfileJson("原老师"));
        var source = ProfileDocument.Parse(ProfileJson());
        var point = source["TimeLayouts"]![LayoutId.ToString()]!["Layouts"]![0]!.AsObject();
        point.Remove("StartTime");
        point.Remove("EndTime");
        point["StartSecond"] = start;
        point["EndSecond"] = end;
        Assert.Empty(ProfileDocument.Validate(source));
        var request = Request(ProfileApplyMode.MergeCurrent);
        request.ProfileJson = ProfileDocument.Serialize(source);
        var result = ProfileApplyExecutor.Apply(request, new ClassIslandProfileApplicationBackend(host));
        Assert.True(result.Success, result.Message);
        Assert.Equal(TimeSpan.FromHours(8), host.Profile.TimeLayouts[LayoutId].Layouts[0].StartTime);
        Assert.Equal(TimeSpan.FromMinutes(525), host.Profile.TimeLayouts[LayoutId].Layouts[0].EndTime);
        Assert.Equal(start, point["StartSecond"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HostAdapterAcceptsBlankCourseAndDefaultSubject(bool useNull)
    {
        using var host = new FakeHost(ProfileJson("原老师"));
        var source = ProfileDocument.Parse(ProfileJson());
        var point = source["TimeLayouts"]![LayoutId.ToString()]!["Layouts"]![0]!.AsObject();
        var course = source["ClassPlans"]![PlanId.ToString()]!["Classes"]![0]!.AsObject();
        point["DefaultClassId"] = useNull ? null : JsonValue.Create("");
        course["SubjectId"] = useNull ? null : JsonValue.Create("");
        var request = Request(ProfileApplyMode.MergeCurrent);
        request.ProfileJson = ProfileDocument.Serialize(source);
        Assert.Empty(ProfileDocument.Validate(source));
        var result = ProfileApplyExecutor.Apply(request, new ClassIslandProfileApplicationBackend(host));
        Assert.True(result.Success, result.Message);
        Assert.Equal(Guid.Empty, host.Profile.TimeLayouts[LayoutId].Layouts[0].DefaultClassId);
        Assert.Equal(Guid.Empty, host.Profile.ClassPlans[PlanId].Classes[0].SubjectId);
    }

    [Fact]
    public void HostAdapterCreatesAndActivatesAndKeepsAutomaticSaving()
    {
        using var host = new FakeHost(ProfileJson("原老师"));
        var oldId = host.Profile.Id;
        var result = ProfileApplyExecutor.Apply(Request(ProfileApplyMode.CreateAndActivate), new ClassIslandProfileApplicationBackend(host));
        Assert.True(result.Success, result.Message);
        Assert.Equal("新档案.json", host.CurrentProfilePath);
        Assert.Equal("新档案.json", host.SettingsService.Settings.SelectedProfile);
        Assert.NotEmpty(host.SettingsService.SavedNotes);
        Assert.NotEqual(oldId, host.Profile.Id);
        Assert.False(host.IsCurrentProfileTrusted);
        host.Profile.Name = "本地继续编辑";
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(FakeHost.ProfilePath, "新档案.json")));
        Assert.Equal("本地继续编辑", saved!["Name"]!.GetValue<string>());
    }

    [Fact]
    public void HostAdapterRemovesFailedNewProfileAndRestoresSelection()
    {
        using var host = new FakeHost(ProfileJson("原老师")) { FailSave = true };
        var original = host.Profile;
        var result = ProfileApplyExecutor.Apply(Request(ProfileApplyMode.CreateAndActivate), new ClassIslandProfileApplicationBackend(host));
        Assert.False(result.Success);
        Assert.Same(original, host.Profile);
        Assert.Equal("原档案.json", host.CurrentProfilePath);
        Assert.Equal("原档案.json", host.SettingsService.Settings.SelectedProfile);
        Assert.True(host.IsCurrentProfileTrusted);
        Assert.False(File.Exists(Path.Combine(FakeHost.ProfilePath, "新档案.json")));
    }

    private static readonly DateTime Today = new(2026, 10, 10);

    /// <summary>服务端档案中的临时层：PlanId 的临时层副本，第一节换成体育，按日期安排（键带宿主可能写出的时区后缀）。</summary>
    private static JsonObject ProfileWithTempLayers(params string[] dates)
    {
        var source = ProfileDocument.Parse(ProfileJson(withExtraSubject: true));
        var ordered = new JsonObject();
        foreach (var date in dates)
        {
            var id = Guid.NewGuid();
            var overlay = source["ClassPlans"]![PlanId.ToString()]!.DeepClone().AsObject();
            overlay["Name"] = $"周一（临时层）{date}";
            overlay["IsOverlay"] = true;
            overlay["OverlaySourceId"] = PlanId.ToString();
            overlay["OverlaySetupTime"] = date + "T00:00:00";
            overlay["Classes"] = new JsonArray(new JsonObject { ["SubjectId"] = OtherSubjectId.ToString() });
            source["ClassPlans"]![id.ToString()] = overlay;
            ordered[date + "T00:00:00+08:00"] = new JsonObject { ["ClassPlanId"] = id.ToString() };
        }
        source["OrderedSchedules"] = ordered;
        return source;
    }

    private static ProfileApplyRequest TempLayerRequest(JsonObject source, bool replace = false) => new()
    {
        Mode = ProfileApplyMode.TempLayers, ReplaceExistingTempLayers = replace,
        ProfileJson = ProfileDocument.BuildTempLayerSelection(ProfileDocument.Serialize(source)),
    };

    private static (Guid Id, JsonObject Plan) LayerOn(JsonObject profile, string date)
    {
        var entry = profile["OrderedSchedules"]!.AsObject().Single(item => item.Key.StartsWith(date, StringComparison.Ordinal));
        var id = Guid.Parse(entry.Value!["ClassPlanId"]!.GetValue<string>());
        return (id, profile["ClassPlans"]![id.ToString()]!.AsObject());
    }

    [Fact]
    public void TempLayerSelectionCarriesOnlyScheduledOverlaysAndTheirDependencies()
    {
        var source = ProfileWithTempLayers("2026-10-12");
        source["OrderedSchedules"]!["2026-10-13T00:00:00"] = new JsonObject { ["ClassPlanId"] = PlanId.ToString() };

        var selected = ProfileDocument.Parse(ProfileDocument.BuildTempLayerSelection(ProfileDocument.Serialize(source)));

        var ordered = Assert.Single(selected["OrderedSchedules"]!.AsObject());
        Assert.StartsWith("2026-10-12", ordered.Key);
        Assert.Equal(2, selected["ClassPlans"]!.AsObject().Count); // 临时层及其来源课表
        Assert.NotNull(selected["Subjects"]![OtherSubjectId.ToString()]);
        Assert.Throws<ArgumentException>(() => ProfileDocument.BuildTempLayerSelection(ProfileJson()));
        Assert.Throws<ArgumentException>(() => ProfileDocument.BuildTempLayerSelection(ProfileDocument.Serialize(source), [PlanId]));
    }

    [Fact]
    public void RegularSelectionExcludesTempLayersAndRejectsThemWhenNamed()
    {
        var source = ProfileWithTempLayers("2026-10-12");
        var overlayId = LayerOn(source, "2026-10-12").Id;

        var selected = ProfileDocument.Parse(ProfileDocument.BuildSelection(ProfileDocument.Serialize(source), ProfileDistributionSection.ClassPlans));

        Assert.Equal(PlanId.ToString(), Assert.Single(selected["ClassPlans"]!.AsObject()).Key);
        Assert.Empty(selected["OrderedSchedules"]!.AsObject());
        var error = Assert.Throws<ArgumentException>(() => ProfileDocument.BuildSelection(ProfileDocument.Serialize(source),
            ProfileDistributionSection.ClassPlans, classPlanIds: [overlayId]));
        Assert.Contains("临时层", error.Message);
    }

    [Fact]
    public void FutureTempLayerIsScheduledWithoutTouchingRegularObjects()
    {
        var result = ProfileDocument.ApplyTempLayers(ProfileJson("原老师"), TempLayerRequest(ProfileWithTempLayers("2026-10-12")), Today);
        var applied = ProfileDocument.Parse(result.Json);

        Assert.Equal((1, 0), (result.Applied, result.Skipped));
        var (_, layer) = LayerOn(applied, "2026-10-12");
        Assert.True(layer["IsOverlay"]!.GetValue<bool>());
        Assert.Equal(PlanId.ToString(), layer["OverlaySourceId"]!.GetValue<string>());
        Assert.Equal(LayoutId.ToString(), layer["TimeLayoutId"]!.GetValue<string>());
        Assert.Equal("2026-10-12T00:00:00", layer["OverlaySetupTime"]!.GetValue<string>());
        Assert.True(layer["Classes"]![0]!["IsChangedClass"]!.GetValue<bool>());
        // 设备缺失的科目补上，已有科目与常规课表都不被改写。
        Assert.NotNull(applied["Subjects"]![OtherSubjectId.ToString()]);
        Assert.Equal("原老师", applied["Subjects"]![SubjectId.ToString()]!["TeacherName"]!.GetValue<string>());
        Assert.Equal(SubjectId.ToString(), applied["ClassPlans"]![PlanId.ToString()]!["Classes"]![0]!["SubjectId"]!.GetValue<string>());
        Assert.Null(applied["OverlayClassPlanId"]);
        // 宿主 GetClassPlanByDate 只有在临时层开关打开时才按日期使用任何临时层，未来日期也需要打开。
        Assert.True(applied["IsOverlayClassPlanEnabled"]!.GetValue<bool>());
        Assert.Single(applied["TimeLayouts"]!.AsObject());
        Assert.Empty(ProfileDocument.Validate(applied));
    }

    [Fact]
    public void TodayTempLayerBecomesTheCurrentOverlay()
    {
        var applied = ProfileDocument.Parse(ProfileDocument.ApplyTempLayers(ProfileJson("原老师"),
            TempLayerRequest(ProfileWithTempLayers("2026-10-10")), Today).Json);

        Assert.Equal(LayerOn(applied, "2026-10-10").Id.ToString(), applied["OverlayClassPlanId"]!.GetValue<string>());
        Assert.True(applied["IsOverlayClassPlanEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void ExpiredTempLayersAreSkippedAndOnlyExpiredIsRejected()
    {
        var result = ProfileDocument.ApplyTempLayers(ProfileJson(), TempLayerRequest(ProfileWithTempLayers("2026-10-09", "2026-10-12")), Today);
        Assert.Equal((1, 1), (result.Applied, result.Skipped));
        Assert.DoesNotContain(ProfileDocument.Parse(result.Json)["OrderedSchedules"]!.AsObject(), item => item.Key.StartsWith("2026-10-09"));

        var error = Assert.Throws<ArgumentException>(() =>
            ProfileDocument.ApplyTempLayers(ProfileJson(), TempLayerRequest(ProfileWithTempLayers("2026-10-09")), Today));
        Assert.Contains("已过去", error.Message);
    }

    [Fact]
    public void SameDayArrangementIsReplacedOnlyWhenConfirmed()
    {
        var device = ProfileWithTemporaryPlans(); // 10-10 已有临时层，10-12 预定了常规课表
        var source = ProfileWithTempLayers("2026-10-10", "2026-10-12");

        var error = Assert.Throws<ArgumentException>(() => ProfileDocument.ApplyTempLayers(device, TempLayerRequest(source), Today));
        Assert.Contains("2026-10-10", error.Message);
        Assert.Contains("2026-10-12", error.Message);

        var applied = ProfileDocument.Parse(ProfileDocument.ApplyTempLayers(device, TempLayerRequest(source, replace: true), Today).Json);
        Assert.Null(applied["ClassPlans"]![OverlayId.ToString()]); // 被替换的旧临时层不再被引用，随之删除
        Assert.NotNull(applied["ClassPlans"]![PlanId.ToString()]); // 被替换的预定常规课表仍保留
        Assert.Equal(OtherSubjectId.ToString(), LayerOn(applied, "2026-10-10").Plan["Classes"]![0]!["SubjectId"]!.GetValue<string>());
        Assert.True(LayerOn(applied, "2026-10-12").Plan["IsOverlay"]!.GetValue<bool>());
        Assert.Equal(2, applied["OrderedSchedules"]!.AsObject().Count);
        Assert.Empty(ProfileDocument.Validate(applied));
    }

    [Fact]
    public void DanglingDeviceDateEntryIsNotTreatedAsAConflict()
    {
        var device = ProfileDocument.Parse(ProfileJson());
        device["OrderedSchedules"] = new JsonObject { ["2026-10-12T00:00:00"] = new JsonObject { ["ClassPlanId"] = Guid.NewGuid().ToString() } };

        var applied = ProfileDocument.Parse(ProfileDocument.ApplyTempLayers(ProfileDocument.Serialize(device),
            TempLayerRequest(ProfileWithTempLayers("2026-10-12")), Today).Json);

        Assert.True(LayerOn(applied, "2026-10-12").Plan["IsOverlay"]!.GetValue<bool>());
    }

    [Fact]
    public void DifferentTimePointsBecomeATemporaryTimeLayoutCopy()
    {
        var source = ProfileWithTempLayers("2026-10-12");
        source["TimeLayouts"]![LayoutId.ToString()]!["Layouts"]![0]!["EndTime"] = "08:40:00";

        var applied = ProfileDocument.Parse(ProfileDocument.ApplyTempLayers(ProfileJson(), TempLayerRequest(source), Today).Json);

        var layoutId = LayerOn(applied, "2026-10-12").Plan["TimeLayoutId"]!.GetValue<string>();
        Assert.NotEqual(LayoutId.ToString(), layoutId);
        var copy = applied["TimeLayouts"]![layoutId]!;
        Assert.True(copy["IsOverlay"]!.GetValue<bool>());
        Assert.Equal(LayoutId.ToString(), copy["OverlaySourceId"]!.GetValue<string>());
        Assert.Equal("工作日（临时层）", copy["Name"]!.GetValue<string>());
        Assert.Equal("08:45:00", applied["TimeLayouts"]![LayoutId.ToString()]!["Layouts"]![0]!["EndTime"]!.GetValue<string>());
        Assert.Empty(ProfileDocument.Validate(applied));
    }

    [Fact]
    public void ExecutorReportsAppliedAndSkippedTempLayers()
    {
        var backend = new FakeBackend(ProfileJson());
        var result = ProfileApplyExecutor.Apply(TempLayerRequest(ProfileWithTempLayers("2026-10-09", "2026-10-12")), backend, today: Today);
        Assert.True(result.Success, result.Message);
        Assert.Equal("已下发 1 个临时层，跳过 1 个已过期的临时层", result.Message);

        var conflict = ProfileApplyExecutor.Apply(TempLayerRequest(ProfileWithTempLayers("2026-10-12")), backend, today: Today);
        Assert.False(conflict.Success);
        Assert.Equal(CommandResultCodes.InvalidRequest, conflict.Code);
    }

    [Fact]
    public void HostAdapterWritesTodayTempLayerAndEnablesOverlay()
    {
        using var host = new FakeHost(ProfileJson("原老师"));
        var result = ProfileApplyExecutor.Apply(TempLayerRequest(ProfileWithTempLayers("2026-10-10")),
            new ClassIslandProfileApplicationBackend(host), today: Today);

        Assert.True(result.Success, result.Message);
        Assert.True(host.Profile.IsOverlayClassPlanEnabled);
        var overlayId = host.Profile.OverlayClassPlanId!.Value;
        Assert.True(host.Profile.ClassPlans[overlayId].IsOverlay);
        Assert.Equal(overlayId, host.Profile.OrderedSchedules[new DateTime(2026, 10, 10)].ClassPlanId);
        Assert.Equal(OtherSubjectId, host.Profile.ClassPlans[overlayId].Classes[0].SubjectId);
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(FakeHost.ProfilePath, "原档案.json")))!;
        Assert.True(saved["IsOverlayClassPlanEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void ReadReturnsCurrentProfileJsonAsData()
    {
        var json = ProfileJson();
        var result = ProfileCollectExecutor.Read(new FakeBackend(json));
        Assert.True(result.Success);
        Assert.Equal(json, result.Data);

        var oversized = ProfileCollectExecutor.Read(new FakeBackend("{\"Comment\":\"" + new string('汉', ProfileDocument.MaxUtf8Bytes / 3 + 1) + "\"}"));
        Assert.False(oversized.Success);
        Assert.Null(oversized.Data);
    }

    [Fact]
    public void SameDateWrittenTwiceIsRejected()
    {
        var profile = ProfileDocument.Parse(ProfileJson());
        profile["OrderedSchedules"] = new JsonObject
        {
            ["2026-10-12T00:00:00"] = new JsonObject { ["ClassPlanId"] = PlanId.ToString() },
            ["2026-10-12T00:00:00+08:00"] = new JsonObject { ["ClassPlanId"] = PlanId.ToString() },
        };
        Assert.Contains(ProfileDocument.Validate(profile), error => error.Contains("多个"));
    }

    [Fact]
    public void CollectedProfileIsNormalizedLikeTheHost()
    {
        var profile = ProfileDocument.Parse(ProfileJson());
        var classes = profile["ClassPlans"]![PlanId.ToString()]!["Classes"]!.AsArray();
        classes.Add(new JsonObject { ["SubjectId"] = SubjectId.ToString() });
        classes.Add(new JsonObject { ["SubjectId"] = SubjectId.ToString() });
        var emptyPlan = Guid.NewGuid();
        profile["ClassPlans"]![emptyPlan.ToString()] = new JsonObject
        {
            ["Name"] = "空课表", ["TimeLayoutId"] = LayoutId.ToString(), ["Classes"] = new JsonArray(),
        };
        profile["OrderedSchedules"] = new JsonObject { ["2026-10-10T00:00:00"] = new JsonObject { ["ClassPlanId"] = Guid.NewGuid().ToString() } };
        profile["OverlayClassPlanId"] = Guid.NewGuid().ToString();
        Assert.NotEmpty(ProfileDocument.Validate(profile));

        var normalized = ProfileDocument.Parse(ProfileDocument.NormalizeCollected(ProfileDocument.Serialize(profile)));

        Assert.Single(normalized["ClassPlans"]![PlanId.ToString()]!["Classes"]!.AsArray());
        Assert.Single(normalized["ClassPlans"]![emptyPlan.ToString()]!["Classes"]!.AsArray());
        Assert.Empty(normalized["OrderedSchedules"]!.AsObject());
        Assert.Empty(ProfileDocument.Validate(normalized));
    }

    private static ProfileApplyRequest Request(ProfileApplyMode mode) => new()
    {
        ProfileJson = ProfileJson(), Sections = ProfileDistributionSection.ClassPlans, Mode = mode, ImportProfileName = "新档案.json",
    };

    private sealed class FakeBackend(string json) : IProfileApplicationBackend
    {
        public string Json { get; private set; } = json;
        public string ActiveName { get; private set; } = "原档案";
        public HashSet<string> Files { get; } = [];
        public bool FailSave { get; init; }
        public int CaptureCount { get; private set; }
        public int RestoreCount { get; private set; }
        public string ReadCurrentJson() => Json;
        public bool ProfileExists(string name) => Files.Contains(name);
        public object CaptureState() { CaptureCount++; return (Json, ActiveName, Files.ToArray()); }
        public void ApplyCurrentJson(string candidate) => Json = candidate;
        public void CreateAndActivate(string candidate, string name) { Json = candidate; ActiveName = name; Files.Add(name); }
        public void Save() { if (FailSave) throw new IOException("磁盘不可写"); }
        public void RestoreState(object snapshot)
        {
            var (originalJson, originalName, originalFiles) = ((string, string, string[]))snapshot;
            Json = originalJson; ActiveName = originalName; Files.Clear(); Files.UnionWith(originalFiles); RestoreCount++;
        }
    }

    // Mirrors the host's public members without implementing IProfileService, whose internal
    // members deliberately prevent third-party mocks. It also exercises the static-field path.
    private sealed class FakeHost : IDisposable
    {
        public static string ProfilePath = string.Empty;
        public Profile Profile { get; set; }
        public string CurrentProfilePath { get; set; } = "原档案.json";
        public FakeSettingsService SettingsService { get; } = new();
        public bool IsCurrentProfileTrusted { get; private set; } = true;
        public bool FailSave { get; init; }
        public FakeHost(string json)
        {
            ProfilePath = Path.Combine(Path.GetTempPath(), "remoteci-profile-tests-" + Guid.NewGuid());
            Directory.CreateDirectory(ProfilePath);
            Profile = JsonSerializer.Deserialize<Profile>(json)!;
            File.WriteAllText(Path.Combine(ProfilePath, CurrentProfilePath), json);
        }
        public void SaveProfile(string filename)
        {
            if (FailSave) throw new IOException("宿主保存失败");
            File.WriteAllText(Path.Combine(ProfilePath, filename), JsonSerializer.Serialize(Profile));
        }
        public void Dispose() => Directory.Delete(ProfilePath, recursive: true);
    }
    // ClassIsland 2.1 只提供 SaveSettings(string note)，没有无参重载。
    private sealed class FakeSettingsService
    {
        public FakeSettings Settings { get; } = new();
        public List<string> SavedNotes { get; } = [];
        public void SaveSettings(string note) => SavedNotes.Add(note);
    }
    private sealed class FakeSettings
    {
        public string SelectedProfile { get; set; } = "原档案.json";
    }
}
