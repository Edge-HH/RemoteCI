using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[AllowAnonymous]
public sealed class VisitorModel(
    VisitorAccessSettings visitorAccess,
    ClassroomService classrooms,
    IStateStore state) : PageModel
{
    public ScheduleBundle? Bundle { get; private set; }

    /// <summary>当前分组筛选下可见的开放访客班级；访客页只能在它们之间切换。</summary>
    public IReadOnlyList<Classroom> VisitorClasses { get; private set; } = [];

    /// <summary>包含开放访客班级的分组；用于访客页按分组筛选。</summary>
    public IReadOnlyList<ClassGroupInfo> VisitorGroups { get; private set; } = [];

    public Guid? SelectedGroupId { get; private set; }

    public Guid CurrentClassId { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid? @class, Guid? group, CancellationToken ct)
    {
        var allVisitorClasses = await visitorAccess.ListVisitorClassroomsAsync(ct);
        // 没有任何班级开放访客时与旧行为一致：直接回登录页。
        if (allVisitorClasses.Count == 0)
            return RedirectToPage("/Login");

        var allGroups = await classrooms.ListGroupsAsync(ct);
        VisitorGroups = ResolveVisitorGroups(allGroups, allVisitorClasses);
        SelectedGroupId = group is { } requested && VisitorGroups.Any(x => x.Id == requested)
            ? requested
            : null;

        VisitorClasses = allVisitorClasses;
        if (SelectedGroupId is { } selectedGroupId)
        {
            // 分组筛选沿用班级管理中的语义：选择父分组时包含全部子分组。
            var classIds = (await classrooms.ResolveTargetClassIdsAsync([], [selectedGroupId], ct)).ToHashSet();
            VisitorClasses = allVisitorClasses.Where(x => classIds.Contains(x.Id)).ToList();
        }

        CurrentClassId = @class is { } id && VisitorClasses.Any(x => x.Id == id)
            ? id
            : VisitorClasses[0].Id;
        Bundle = state.GetLatestSchedule(CurrentClassId);
        return Page();
    }

    /// <summary>只保留能筛出开放访客班级的分组，并补上这些分组的祖先以便层级筛选。</summary>
    private static IReadOnlyList<ClassGroupInfo> ResolveVisitorGroups(
        IReadOnlyList<ClassGroupInfo> allGroups,
        IReadOnlyList<Classroom> visitorClasses)
    {
        var groupsById = allGroups.ToDictionary(x => x.Id);
        var includedIds = visitorClasses
            .SelectMany(classroom => classroom.GroupAssignments.Select(assignment => assignment.GroupId))
            .ToHashSet();

        foreach (var groupId in includedIds.ToList())
        {
            var parentId = groupsById.GetValueOrDefault(groupId)?.ParentId;
            while (parentId is { } parent && includedIds.Add(parent))
                parentId = groupsById.GetValueOrDefault(parent)?.ParentId;
        }

        return allGroups
            .Where(group => includedIds.Contains(group.Id))
            .OrderBy(group => group.Depth)
            .ThenBy(group => group.Name, StringComparer.CurrentCulture)
            .ToList();
    }
}
