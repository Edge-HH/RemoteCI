using System.Text.Json;
using System.Text.Json.Nodes;
using ClassIsland.Shared.Models.Profile;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class SubjectLocationExecutorTests
{
    private sealed class FakeProfileOps : IProfileWriteOperations
    {
        public Dictionary<Guid, Subject> Subjects { get; } = [];
        public Dictionary<Guid, ClassPlan> ClassPlans { get; } = [];
        public int SaveCount { get; private set; }
        public bool ThrowOnSave { get; set; }
        IReadOnlyDictionary<Guid, Subject> IProfileWriteOperations.Subjects => Subjects;
        IReadOnlyDictionary<Guid, ClassPlan> IProfileWriteOperations.ClassPlans => ClassPlans;
        public Guid? CreateTempClassPlan(Guid sourcePlanId, DateTime? enableDateTime = null) => null;
        public void SaveProfile()
        {
            if (ThrowOnSave) throw new IOException("disk full");
            SaveCount++;
        }
    }

    private static SubjectLocationRequest Request(Guid subjectId, string? location) => new() { SubjectId = subjectId, Location = location };

    [Fact]
    public void Validate_RejectsMissingSubjectAndOverlongLocation()
    {
        Assert.NotNull(SubjectLocationExecutor.Validate(null));
        Assert.NotNull(SubjectLocationExecutor.Validate(Request(Guid.Empty, "机房")));
        Assert.NotNull(SubjectLocationExecutor.Validate(Request(Guid.NewGuid(), new string('房', SubjectLocations.MaxLength + 1))));
        Assert.Null(SubjectLocationExecutor.Validate(Request(Guid.NewGuid(), "机房")));
        Assert.Null(SubjectLocationExecutor.Validate(Request(Guid.NewGuid(), null)));
    }

    [Fact]
    public void Apply_WritesTrimsAndClearsLocationInAttachedObjects()
    {
        var profile = new FakeProfileOps();
        var subjectId = Guid.NewGuid();
        var other = Guid.NewGuid();
        var subject = new Subject { Name = "信息" };
        subject.AttachedObjects[other] = "keep";
        profile.Subjects[subjectId] = subject;

        var set = SubjectLocationExecutor.Apply(Request(subjectId, "  机房  "), profile);

        Assert.True(set.Success);
        Assert.Equal("机房", SubjectLocationExecutor.Read(subject));
        Assert.Equal(1, profile.SaveCount);
        // 序列化后的档案 JSON 能被服务端按同一格式读出。
        var json = JsonNode.Parse(JsonSerializer.Serialize(subject))!.AsObject();
        Assert.Equal("机房", SubjectLocations.Read(json));

        var unchanged = SubjectLocationExecutor.Apply(Request(subjectId, "机房"), profile);
        Assert.True(unchanged.Success);
        Assert.Equal(1, profile.SaveCount);

        var clear = SubjectLocationExecutor.Apply(Request(subjectId, " "), profile);
        Assert.True(clear.Success);
        Assert.Null(SubjectLocationExecutor.Read(subject));
        Assert.False(subject.AttachedObjects.ContainsKey(SubjectLocations.AttachedObjectId));
        Assert.Equal("keep", subject.AttachedObjects[other]);
        Assert.Equal(2, profile.SaveCount);

        Assert.Equal(CommandResultCodes.InvalidRequest, SubjectLocationExecutor.Apply(Request(Guid.NewGuid(), "机房"), profile).Code);
    }

    [Fact]
    public void Apply_RestoresPreviousLocationWhenSaveFails()
    {
        var profile = new FakeProfileOps { ThrowOnSave = true };
        var subjectId = Guid.NewGuid();
        var subject = new Subject { Name = "物理" };
        subject.WriteAttachedObject(SubjectLocations.AttachedObjectId, new SubjectLocationAttachment { Location = "实验室" });
        profile.Subjects[subjectId] = subject;
        Exception? logged = null;

        var result = SubjectLocationExecutor.Apply(Request(subjectId, "操场"), profile, ex => logged = ex);

        Assert.False(result.Success);
        Assert.Equal(CommandResultCodes.SaveFailed, result.Code);
        Assert.IsType<IOException>(logged);
        Assert.Equal("实验室", SubjectLocationExecutor.Read(subject));
    }

    [Fact]
    public void Read_AcceptsDeserializedJsonFromHostProfile()
    {
        var subject = new Subject { Name = "体育" };
        subject.AttachedObjects[SubjectLocations.AttachedObjectId] = JsonDocument.Parse("""{"location":" 操场 "}""").RootElement;
        Assert.Equal("操场", SubjectLocationExecutor.Read(subject));

        subject.AttachedObjects[SubjectLocations.AttachedObjectId] = JsonDocument.Parse("[1,2]").RootElement;
        Assert.Null(SubjectLocationExecutor.Read(subject));
    }
}
