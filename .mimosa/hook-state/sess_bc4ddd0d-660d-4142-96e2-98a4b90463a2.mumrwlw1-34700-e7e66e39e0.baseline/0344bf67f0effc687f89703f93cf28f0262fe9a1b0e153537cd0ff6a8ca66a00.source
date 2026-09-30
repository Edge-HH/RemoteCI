using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 敏感文件防护：在 Windows 上断开继承并重建最小 ACL（当前用户可改 + SYSTEM/管理员），
/// 防止同机其他账号读取插件凭据（Settings.json）与设备验证器（Accounts.json）。
/// 只移除 Users 组规则的做法无法清理继承而来的其它宽泛规则，断继承才能保证结果可预测。
/// </summary>
internal static class FileProtection
{
    /// <summary>由 RemoteCiService 启动时注入；ACL 收紧失败记 Warning 而非静默吞掉。</summary>
    internal static ILogger? Logger { get; set; }

    public static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return;
        RestrictCore(path);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RestrictCore(string path)
    {
        try
        {
            var file = new FileInfo(path);
            var security = file.GetAccessControl();
            // 断开继承并丢弃全部继承规则，再清空显式规则，从零重建最小 ACL。
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                         .OfType<FileSystemAccessRule>()
                         .ToList())
            {
                security.RemoveAccessRule(rule);
            }

            var current = WindowsIdentity.GetCurrent().User;
            if (current is not null)
                security.AddAccessRule(new FileSystemAccessRule(
                    current, FileSystemRights.Modify, AccessControlType.Allow));
            // 保留 SYSTEM 与管理员：否则系统备份/故障恢复会完全无法触碰文件。
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(security);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            PlatformNotSupportedException or InvalidOperationException or ArgumentException)
        {
            // ACL 收紧失败不影响插件功能，但必须留下可诊断的痕迹：少一层纵深防御时
            // 明文凭据可能被同机其它账号读取。
            Logger?.LogWarning(ex, "文件 ACL 收紧失败，凭据可能被同机其它账号读取：{Path}", path);
        }
    }
}
