namespace RemoteCI.Server.Data;

/// <summary>
/// 班级最近一次从插件同步的课表、扩展功能与扩展分组（含插件设置当前值）的持久化副本。
/// 服务端重启后插件尚未重连时，换课、个人日程、提醒与扩展设置页仍按这份副本工作；
/// 插件重新连接并同步后整体覆盖。课程实时状态与课堂事件只在内存中，不在此保存。
/// </summary>
public sealed class ClassStateCache
{
    public Guid ClassroomId { get; set; }
    public Classroom Classroom { get; set; } = null!;

    /// <summary>插件原样上报的课表（不含换课临时任课老师覆盖，覆盖另存于 LessonTeacherOverrides）。</summary>
    public string? ScheduleJson { get; set; }

    public string? ExtensionsJson { get; set; }
    public string? ExtensionGroupsJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
