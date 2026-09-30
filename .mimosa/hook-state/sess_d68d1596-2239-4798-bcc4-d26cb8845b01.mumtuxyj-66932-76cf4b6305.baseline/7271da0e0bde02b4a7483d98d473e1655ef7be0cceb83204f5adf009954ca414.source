using RemoteCI.Plugin.Services;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class CloudTokenStoreTests
{
    [Fact]
    public void SaveLoad_RoundTripsWithoutStoringPlaintext()
    {
        var path = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "token.bin");
        var store = new CloudTokenStore(path);
        try
        {
            const string token = "base64-token-with-+/=-characters";
            store.Save(token);

            Assert.Equal(token, store.Load());
            // 落盘内容绝不能直接包含明文凭据（Windows 走 DPAPI，密文是 Base64 包裹的密文块）。
            if (OperatingSystem.IsWindows())
            {
                var persisted = File.ReadAllText(path);
                Assert.DoesNotContain(token, persisted, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void SaveNull_RemovesStoredToken()
    {
        var path = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "token.bin");
        var store = new CloudTokenStore(path);
        try
        {
            store.Save("doomed-token");
            Assert.NotNull(store.Load());

            store.Save(null); // 吊销。

            Assert.False(File.Exists(path));
            Assert.Null(store.Load());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_MissingOrCorruptedFile_ReturnsNull()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"));
        var missing = new CloudTokenStore(Path.Combine(directory, "absent.bin"));
        Assert.Null(missing.Load());

        var corruptedPath = Path.Combine(directory, "corrupt.bin");
        Directory.CreateDirectory(directory);
        File.WriteAllText(corruptedPath, "not-base64!");
        try
        {
            Assert.Null(new CloudTokenStore(corruptedPath).Load());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TryMigrateLegacyPlaintext_ReadsOldSettingsField()
    {
        var path = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "Settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, """{"CloudToken":"legacy-plain-token","LanServerPort":8765}""");

            Assert.Equal("legacy-plain-token", CloudTokenStore.TryMigrateLegacyPlaintext(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
