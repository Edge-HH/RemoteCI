using System.Security.AccessControl;
using System.Security.Principal;
using RemoteCI.Plugin.Services;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class FileProtectionTests
{
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void RestrictToCurrentUser_RemovesBuiltinUsersReadAccess()
    {
        if (!OperatingSystem.IsWindows()) return; // ACL 收紧逻辑仅在 Windows 生效，非 Windows 静默降级。

        var path = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "secret.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "secret");
        try
        {
            FileProtection.RestrictToCurrentUser(path);

            var security = new FileInfo(path).GetAccessControl();
            var builtinUsers = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            Assert.DoesNotContain(
                security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                    .OfType<FileSystemAccessRule>(),
                rule => rule.IdentityReference == builtinUsers &&
                    rule.AccessControlType == AccessControlType.Allow);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
