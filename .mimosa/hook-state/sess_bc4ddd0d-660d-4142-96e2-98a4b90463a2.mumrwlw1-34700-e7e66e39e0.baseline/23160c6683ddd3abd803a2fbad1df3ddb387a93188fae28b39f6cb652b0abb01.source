using System.Collections.Concurrent;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace RemoteCI.Plugin;

/// <summary>
/// 宿主 API 兼容层：ClassIsland 2.2 预览版（2.1.1.x）改了少量公开 API 的类型，
/// 例如 Profile 的字典实现、FluentAvalonia 的 FontIcon 基类、Avalonia 的窗口装饰枚举。
/// 直接编译绑定会让同一份插件包在旧宿主可用、在新宿主抛 MissingMethodException，
/// 因此这些成员改为按成员名反射访问，让新旧宿主共用同一条代码路径。
/// </summary>
internal static class HostApiCompat
{
    private static readonly ConcurrentDictionary<(Type DeclaringType, string MemberName), PropertyInfo?> Properties = new();
    private static readonly ConcurrentDictionary<Type, MethodInfo> BindMethods = new();

    /// <summary>读取属性并转换成目标接口或类型；属性返回类型在新旧宿主中不同也可以使用。</summary>
    /// <exception cref="MissingMemberException">宿主类型没有该名称的公开实例属性。</exception>
    /// <exception cref="InvalidCastException">属性值无法转换成目标类型。</exception>
    public static T ReadProperty<T>(object instance, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var value = ResolveProperty(instance.GetType(), propertyName).GetValue(instance);
        return value is T typed
            ? typed
            : throw new InvalidCastException(
                $"{instance.GetType().FullName}.{propertyName} 不是 {typeof(T).FullName}（实际 {value?.GetType().FullName ?? "null"}）");
    }

    /// <summary>按属性名写入值；属性声明在新旧宿主的不同基类型上也可以使用。</summary>
    /// <exception cref="MissingMemberException">宿主类型没有该名称的公开实例属性。</exception>
    public static void WriteProperty(object instance, string propertyName, object? value)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ResolveProperty(instance.GetType(), propertyName).SetValue(instance, value);
    }

    /// <summary>
    /// 把窗口设为无装饰窗口：Avalonia 12 把枚举 <c>SystemDecorations</c> 改名为 <c>WindowDecorations</c>，
    /// 属性名保持不变，因此按属性类型动态解析枚举值即可同时兼容两代宿主。
    /// </summary>
    public static void RemoveWindowDecorations(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        SetEnumProperty(window, "None", "WindowDecorations", "SystemDecorations");
    }

    /// <summary>
    /// 绑定动态资源（例如主题字体与颜色）：Avalonia 12 把 <c>Bind</c> 的第二个参数
    /// 从 <c>IBinding</c> 改成 <c>BindingBase</c>，直接编译绑定会在新宿主抛 MissingMethodException。
    /// 这里按运行时参数类型挑选可用的重载，新旧宿主都能命中。
    /// </summary>
    public static void BindDynamicResource(AvaloniaObject target, AvaloniaProperty property, string resourceKey)
    {
        ArgumentNullException.ThrowIfNull(target);
        object binding = new DynamicResourceExtension(resourceKey);
        ResolveBindMethod(target.GetType(), property, binding).Invoke(target, [property, binding]);
    }

    /// <summary>按枚举成员名给枚举属性赋值，用于兼容宿主把枚举类型改名的情况。</summary>
    internal static void SetEnumProperty(object instance, string valueName, params string[] propertyNames)
    {
        ArgumentNullException.ThrowIfNull(instance);

        foreach (var propertyName in propertyNames)
        {
            var property = ResolvePropertyOrNull(instance.GetType(), propertyName);
            if (property is null)
            {
                continue;
            }

            property.SetValue(instance, Enum.Parse(property.PropertyType, valueName));
            return;
        }

        throw new MissingMemberException(instance.GetType().FullName, string.Join("/", propertyNames));
    }

    private static PropertyInfo ResolveProperty(Type type, string propertyName) =>
        ResolvePropertyOrNull(type, propertyName) ?? throw new MissingMemberException(type.FullName, propertyName);

    private static MethodInfo ResolveBindMethod(Type targetType, object property, object binding) =>
        BindMethods.GetOrAdd(targetType, type =>
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "Bind" || method.IsGenericMethodDefinition)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length == 2
                    && parameters[0].ParameterType.IsInstanceOfType(property)
                    && parameters[1].ParameterType.IsInstanceOfType(binding))
                {
                    return method;
                }
            }

            throw new MissingMemberException(targetType.FullName, "Bind(AvaloniaProperty, IBinding/BindingBase)");
        });

    // GetProperty 会沿继承链查找，可覆盖属性声明在新旧宿主不同基类上的情况；结果按类型缓存，
    // 避免状态收集这类周期性调用重复反射。
    private static PropertyInfo? ResolvePropertyOrNull(Type type, string propertyName) =>
        Properties.GetOrAdd(
            (type, propertyName),
            static key => key.DeclaringType.GetProperty(
                key.MemberName,
                BindingFlags.Public | BindingFlags.Instance));
}
