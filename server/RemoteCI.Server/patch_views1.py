s = open('Pages/BatchControl.cshtml', encoding='utf-8').read()


def rep(old, new):
    global s
    assert old in s, 'NOT FOUND: ' + old[:70]
    s = s.replace(old, new, 1)


# target list: groups as chips (multi-group), no ambiguous section headers
rep('''        <h3>逐班勾选</h3>
        <div class="broadcast-targets">
            @{
                var lastGroup = "\\u0000";
                foreach (var classroom in Model.Classes)
                {
                    if (!string.Equals(lastGroup, classroom.GroupName ?? string.Empty, StringComparison.Ordinal))
                    {
                        lastGroup = classroom.GroupName ?? string.Empty;
                        <p class="broadcast-group-label">@(lastGroup.Length == 0 ? "未分组" : lastGroup)</p>
                    }
                    <label class="check">
                        <input type="checkbox" name="SelectedClassIds" value="@classroom.Id" data-voice-query />
                        @classroom.Name
                        <span class="muted">@(classroom.PluginCount > 0 ? "在线" : "离线")</span>
                    </label>
                }
            }
        </div>''',
    '''        <h3>逐班勾选</h3>
        <div class="broadcast-targets">
            @foreach (var classroom in Model.Classes)
            {
                <label class="check">
                    <input type="checkbox" name="SelectedClassIds" value="@classroom.Id" data-voice-query />
                    @classroom.Name
                    <span class="muted">@(classroom.PluginCount > 0 ? "在线" : "离线")</span>
                    @if (classroom.GroupNames is { Count: > 0 })
                    {
                        foreach (var groupName in classroom.GroupNames)
                        {
                            <span class="group-name">@groupName</span>
                        }
                    }
                </label>
            }
        </div>''')

with open('Pages/BatchControl.cshtml', 'w', encoding='utf-8', newline='') as fh:
    fh.write(s)
print('BatchControl ok')

# ClassSelect: group chips + avatar
s = open('Pages/ClassSelect.cshtml', encoding='utf-8').read()
rep('''            <button type="submit">
                <strong>@classroom.Name</strong>
                <span class="group-name">@(classroom.GroupName ?? "未分组")</span>
                <small>@classroom.RoleName · 访客功能@(classroom.VisitorEnabled ? "已开放" : "未开放")</small>
            </button>''',
    '''            <button type="submit">
                @if (classroom.HasAvatar == true)
                {
                    <img class="class-avatar" src="/api/classes/@classroom.Id/avatar" alt="" />
                }
                <strong>@classroom.Name</strong>
                <span>
                    @if (classroom.GroupNames is { Count: > 0 })
                    {
                        foreach (var groupName in classroom.GroupNames)
                        {
                            <span class="group-name">@groupName</span>
                        }
                    }
                </span>
                <small>@classroom.RoleName · 访客功能@(classroom.VisitorEnabled ? "已开放" : "未开放")</small>
            </button>''')
with open('Pages/ClassSelect.cshtml', 'w', encoding='utf-8', newline='') as fh:
    fh.write(s)
print('ClassSelect ok')

# Control: broadcast targets chips + class settings panel
s = open('Pages/Control.cshtml', encoding='utf-8').read()
rep('''                <div class="broadcast-targets" aria-label="选择要通知的班级">
                    @{
                        var lastGroup = "\\u0000";
                        foreach (var target in Model.BroadcastTargets)
                        {
                            if (!string.Equals(lastGroup, target.GroupName ?? string.Empty, StringComparison.Ordinal))
                            {
                                lastGroup = target.GroupName ?? string.Empty;
                                <p class="broadcast-group-label">@(lastGroup.Length == 0 ? "未分组" : lastGroup)</p>
                            }
                            <label class="check">
                                <input type="checkbox" name="BroadcastClassIds" value="@target.Id" />
                                @target.Name
                            </label>
                        }
                    }
                </div>''',
    '''                <div class="broadcast-targets" aria-label="选择要通知的班级">
                    @foreach (var target in Model.BroadcastTargets)
                    {
                        <label class="check">
                            <input type="checkbox" name="BroadcastClassIds" value="@target.Id" />
                            @target.Name
                            @if (target.GroupNames is { Count: > 0 })
                            {
                                foreach (var groupName in target.GroupNames)
                                {
                                    <span class="group-name">@groupName</span>
                                }
                            }
                        </label>
                    }
                </div>''')
with open('Pages/Control.cshtml', 'w', encoding='utf-8', newline='') as fh:
    fh.write(s)
print('Control targets ok')

# Control.cshtml.cs: drop GroupName ordering
s = open('Pages/Control.cshtml.cs', encoding='utf-8').read()
rep('''        BroadcastTargets = AccessibleClasses
            .Where(x => x.Id != CurrentClassId && x.Permissions?.HasFlag(UserPermissions.SendNotifications) == true)
            .OrderBy(x => x.GroupName).ThenBy(x => x.Name)
            .ToList();''',
    '''        BroadcastTargets = AccessibleClasses
            .Where(x => x.Id != CurrentClassId && x.Permissions?.HasFlag(UserPermissions.SendNotifications) == true)
            .OrderBy(x => x.Name)
            .ToList();''')
with open('Pages/Control.cshtml.cs', 'w', encoding='utf-8', newline='') as fh:
    fh.write(s)
print('Control model ok')
