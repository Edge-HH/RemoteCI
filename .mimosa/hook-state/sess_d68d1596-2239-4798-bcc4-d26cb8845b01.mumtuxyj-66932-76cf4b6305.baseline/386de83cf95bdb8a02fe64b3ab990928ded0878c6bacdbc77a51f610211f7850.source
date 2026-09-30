using ClassIsland.Shared.Models.Profile;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ScheduleMutationTests
{
    [Fact]
    public void SchedulePullRequest_TriggersFreshScheduleCollection()
    {
        ScheduleSyncRequest? requested = null;
        var handler = new SchedulePullRequestHandler(request =>
        {
            requested = request;
            return new ScheduleSyncStatus
            {
                TaskId = request.TaskId,
                Source = request.Source,
                State = ScheduleSyncTaskState.Running,
            };
        });

        var envelope = Envelope.SchedulePull();
        var handled = handler.TryHandle(envelope);

        Assert.True(handled);
        Assert.NotNull(requested);
        Assert.Equal(envelope.MessageId, requested.TaskId);
    }

    [Fact]
    public void ExchangeCanBeRolledBackAfterSaveFailure()
    {
        var math = Guid.NewGuid();
        var physics = Guid.NewGuid();
        var courses = new[]
        {
            new ClassInfo { SubjectId = math, IsChangedClass = false },
            new ClassInfo { SubjectId = physics, IsChangedClass = true },
        };
        var request = new ScheduleChangeRequest
        {
            Mode = ScheduleChangeMode.Exchange,
            SourceIndex = 0,
            TargetIndex = 1,
        };

        var mutation = ScheduleMutation.Create(courses, request);
        mutation.Apply();
        Assert.Equal(physics, courses[0].SubjectId);
        Assert.Equal(math, courses[1].SubjectId);
        Assert.All(courses, course => Assert.True(course.IsChangedClass));

        mutation.Rollback();
        Assert.Equal(math, courses[0].SubjectId);
        Assert.Equal(physics, courses[1].SubjectId);
        Assert.False(courses[0].IsChangedClass);
        Assert.True(courses[1].IsChangedClass);
    }

    [Fact]
    public void ReplaceCanBeRolledBackAfterSaveFailure()
    {
        var math = Guid.NewGuid();
        var history = Guid.NewGuid();
        var courses = new[] { new ClassInfo { SubjectId = math } };
        var request = new ScheduleChangeRequest
        {
            Mode = ScheduleChangeMode.Replace,
            SourceIndex = 0,
            ReplacementSubjectId = history,
        };

        var mutation = ScheduleMutation.Create(courses, request);
        mutation.Apply();
        Assert.Equal(history, courses[0].SubjectId);
        Assert.True(courses[0].IsChangedClass);

        mutation.Rollback();
        Assert.Equal(math, courses[0].SubjectId);
        Assert.False(courses[0].IsChangedClass);
    }

    [Theory]
    [InlineData(ScheduleChangeMode.Exchange, -1, 1, false, "原节次超出课表范围")]
    [InlineData(ScheduleChangeMode.Exchange, 0, 0, false, "目标节次无效")]
    [InlineData(ScheduleChangeMode.Exchange, 0, 3, false, "目标节次无效")]
    [InlineData(ScheduleChangeMode.Replace, 0, null, true, "替换科目不存在")]
    public void InvalidMutationIsRejectedBeforeCreatingOverlay(
        ScheduleChangeMode mode,
        int sourceIndex,
        int? targetIndex,
        bool hasReplacementSubject,
        string expectedError)
    {
        var replacementSubjectId = hasReplacementSubject ? Guid.NewGuid() : (Guid?)null;
        var request = new ScheduleChangeRequest
        {
            Mode = mode,
            SourceIndex = sourceIndex,
            TargetIndex = targetIndex,
            ReplacementSubjectId = replacementSubjectId,
        };

        var error = ScheduleMutation.Validate(2, request, _ => false);

        Assert.Equal(expectedError, error);
    }
}
