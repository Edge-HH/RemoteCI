using System.Reflection;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Shared.Models.Profile;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// “启用课表”开关是宿主 LessonsService 的公开运行时属性（不在 ILessonsService 接口上、也不落盘），
/// 因此按名称反射访问；宿主改名或移除时返回 null，放假日处理自动降级。
/// </summary>
public static class ClassPlanSwitch
{
    public static PropertyInfo? Resolve(object lessons) =>
        lessons.GetType().GetProperty("IsClassPlanEnabled", BindingFlags.Instance | BindingFlags.Public) is
            { CanRead: true, CanWrite: true } property && property.PropertyType == typeof(bool)
            ? property
            : null;
}

public sealed class ClassIslandHolidayHost(ILessonsService lessons) : IHolidayHostOperations
{
    private readonly PropertyInfo? _switch = ClassPlanSwitch.Resolve(lessons);

    public bool? IsClassPlanEnabled
    {
        get => _switch?.GetValue(lessons) as bool?;
        set
        {
            if (value is { } enabled) _switch?.SetValue(lessons, enabled);
        }
    }
}
