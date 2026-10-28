using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>插件市场索引、服务端预下载与“从 CIPX 安装”的插件包分发。</summary>
public sealed class PluginPackageTests
{
    [Fact]
    public void Catalog_ParsesIndexAndFiltersOldApiPlugins()
    {
        using var zip = IndexZip(
            Item("demo.a", "Demo A", "1.2.0.0", "2.0.0.0", downloads: 50, stars: 3, dependencies: [("demo.dep", true), ("demo.optional", false)]),
            Item("demo.dep", "Dep", "1.0.0.0", "2.0.0.0", downloads: 10),
            Item("legacy.plugin", "Legacy", "1.0.0.0", "1.4.0.0", downloads: 999));

        var index = PluginMarketCatalog.Parse(zip, "ghproxy", DateTimeOffset.UnixEpoch);

        Assert.Equal(["demo.a", "demo.dep"], index.Plugins.Select(x => x.Id));
        var plugin = index.Find("DEMO.A")!;
        Assert.Equal((50L, 3L), (plugin.DownloadCount, plugin.StarsCount));
        // 配置的镜像优先，github 次之，其余镜像兜底。
        Assert.Equal(
            ["https://mirror.example/https://github.com/o/demo.a/demo.a.cipx", "https://github.com/o/demo.a/demo.a.cipx"],
            plugin.DownloadUrls);
    }

    [Fact]
    public void Catalog_ResolvesRequiredDependenciesFirst()
    {
        using var zip = IndexZip(
            Item("demo.a", "A", "1.0", "2.0.0.0", dependencies: [("demo.dep", true), ("demo.optional", false)]),
            Item("demo.dep", "Dep", "1.0", "2.0.0.0", dependencies: [("demo.base", true)]),
            Item("demo.base", "Base", "1.0", "2.0.0.0"));
        var index = PluginMarketCatalog.Parse(zip, null, DateTimeOffset.UnixEpoch);

        var set = PluginMarketCatalog.ResolveInstallSet(index, ["demo.a"]);

        Assert.Equal(["demo.base", "demo.dep", "demo.a"], set.Select(x => x.Plugin.Id));
        Assert.Equal([true, true, false], set.Select(x => x.IsDependency));
        Assert.Throws<PluginMarketException>(() => PluginMarketCatalog.ResolveInstallSet(index, ["missing.plugin"]));
    }

    [Fact]
    public async Task PreDownload_VerifiesMd5FallsBackToNextMirrorAndReusesCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var package = Cipx("demo.a", "Demo A", "1.2.0.0");
            var md5 = Convert.ToHexString(MD5.HashData(package));
            var handler = new FakeHandler(request => request.RequestUri!.Host == "mirror.example"
                ? new HttpResponseMessage(HttpStatusCode.BadGateway)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) });
            var service = new PluginPackageService(CreateTransfers(root), new FakeHttpClientFactory(handler),
                NullLogger<PluginPackageService>.Instance);
            var plugin = new MarketPlugin("demo.a", "Demo A", "", "", "1.2.0.0", 0, 0, null, null,
                ["https://mirror.example/a.cipx", "https://github.com/a.cipx"], md5, []);
            var owner = new UserProfile { Id = Guid.NewGuid(), DisplayName = "管理员", Role = UserRole.Admin };

            var first = Assert.Single(await service.PrepareFromMarketAsync([(plugin, false)], owner));
            Assert.Equal(("demo.a", "1.2.0.0"), (first.Manifest.Id, first.Manifest.Version));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(package)), first.Record.Sha256);
            Assert.Equal(2, handler.Requests);

            var second = Assert.Single(await service.PrepareFromMarketAsync([(plugin, false)], owner));
            Assert.Equal(first.Record.Id, second.Record.Id);
            Assert.Equal(2, handler.Requests);

            var tampered = plugin with { Version = "1.3.0.0", DownloadMd5 = new string('0', 32) };
            await Assert.ThrowsAsync<PluginMarketException>(() => service.PrepareFromMarketAsync([(tampered, false)], owner));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task UploadedCipx_IsDispatchedAsPluginPackageCommand()
    {
        await using var factory = new TestWebApplicationFactory();
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginWebUiAsync(browser);
        var html = await browser.GetStringAsync("/BatchControl");
        Assert.Contains(@"data-batch-open=""InstallPluginPackages""", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"").Groups[1].Value);

        var invalid = await UploadAsync(browser, token, "notes.cipx", Encoding.UTF8.GetBytes("not a zip"));
        Assert.False(invalid.GetProperty("success").GetBoolean());

        var package = Cipx("demo.a", "Demo A", "1.2.0.0");
        var uploaded = (await UploadAsync(browser, token, "Demo.cipx", package)).GetProperty("package");
        Assert.Equal("demo.a", uploaded.GetProperty("pluginId").GetString());
        Assert.Equal("1.2.0.0", uploaded.GetProperty("version").GetString());
        var transferId = uploaded.GetProperty("transferId").GetString()!;

        using var plugin = await ConnectPluginAsync(factory);
        var execute = browser.PostAsync("/BatchControl?handler=Execute", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Operation"] = nameof(BatchOperationKind.InstallPluginPackages),
            ["SelectedClassIds"] = TestWebApplicationFactory.DefaultClassId.ToString(),
            ["PluginPackageTransferIds"] = transferId,
            ["GrantExtensionAccess"] = "true",
            ["ConfirmHighRisk"] = "true",
            ["__RequestVerificationToken"] = token,
        }));
        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        var command = JsonSerializer.Deserialize<CommandMessage>(JsonSerializer.Serialize(forwarded.Payload), JsonDefaults.Options)!;
        Assert.Equal(CommandKind.InstallPluginPackages, command.Command);
        var request = command.PluginPackages!;
        Assert.Equal(PluginPackageInstallMode.Install, request.Mode);
        Assert.True(request.GrantExtensionAccess);
        var reference = Assert.Single(request.Packages);
        Assert.Equal(("demo.a", "1.2.0.0", transferId), (reference.PluginId, reference.Version, reference.TransferId));
        Assert.Null(FileDistributionRequest.ValidateTransfer(reference.ToTransferRequest()));
        await SendAsync(plugin, new Envelope
        {
            Type = Protocol.MessageTypeCommandResult,
            ReplyToMessageId = forwarded.MessageId,
            Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "开始接收" },
        });
        Assert.Equal(HttpStatusCode.Redirect, (await execute).StatusCode);

        // 命令下发时已为目标设备登记下载权限，插件可凭下载凭据取回插件包。
        using var download = new HttpRequestMessage(HttpMethod.Get, $"/api/plugin/files/{transferId}");
        download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await factory.GetPluginTokenAsync());
        download.Headers.Add(FileTransferEndpoints.TransferKeyHeader, reference.DownloadKey);
        var response = await factory.CreateClient().SendAsync(download);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(package, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ExtensionAccessAndGrantOnInstall_AreDispatchedToPlugin()
    {
        await using var factory = new TestWebApplicationFactory();
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginWebUiAsync(browser);
        var html = await browser.GetStringAsync("/BatchControl");
        Assert.Contains(@"data-batch-open=""SetExtensionAccess""", html);
        Assert.Contains(@"name=""GrantExtensionAccess""", html);
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"").Groups[1].Value);
        using var plugin = await ConnectPluginAsync(factory);

        async Task<CommandMessage> ExecuteAsync(Dictionary<string, string> form)
        {
            form["SelectedClassIds"] = TestWebApplicationFactory.DefaultClassId.ToString();
            form["ConfirmHighRisk"] = "true";
            form["__RequestVerificationToken"] = token;
            var execute = browser.PostAsync("/BatchControl?handler=Execute", new FormUrlEncodedContent(form));
            var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
            await SendAsync(plugin, new Envelope
            {
                Type = Protocol.MessageTypeCommandResult,
                ReplyToMessageId = forwarded.MessageId,
                Payload = new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "完成" },
            });
            Assert.Equal(HttpStatusCode.Redirect, (await execute).StatusCode);
            return JsonSerializer.Deserialize<CommandMessage>(JsonSerializer.Serialize(forwarded.Payload), JsonDefaults.Options)!;
        }

        var access = await ExecuteAsync(new Dictionary<string, string>
        {
            ["Operation"] = nameof(BatchOperationKind.SetExtensionAccess),
            ["ExtensionAccessAction"] = nameof(ExtensionAccessAction.Lock),
            ["PluginIdsText"] = "demo.a\nremoteci.plugin\ndemo.b",
        });
        Assert.Equal(CommandKind.SetExtensionAccess, access.Command);
        Assert.Equal(ExtensionAccessAction.Lock, access.ExtensionAccess!.Action);
        Assert.Equal(["demo.a", "demo.b"], access.ExtensionAccess.PluginIds);

        var install = await ExecuteAsync(new Dictionary<string, string>
        {
            ["Operation"] = nameof(BatchOperationKind.InstallPlugins),
            ["PluginAction"] = nameof(PluginActionKind.Install),
            ["PluginIdsText"] = "demo.a",
            ["GrantExtensionAccess"] = "true",
        });
        Assert.Equal(CommandKind.InstallPlugins, install.Command);
        Assert.True(install.PluginManagement!.GrantExtensionAccess);
    }

    [Fact]
    public async Task UpgradeDialog_ListsOnlyPluginsWithUpdates()
    {
        await using var factory = new TestWebApplicationFactory();
        using var plugin = await ConnectPluginAsync(factory);
        await SendAsync(plugin, Envelope.SoftwareInventory(new SoftwareInventory
        {
            DeviceName = "教室一体机",
            Plugins =
            [
                new SoftwarePackageInfo { Id = "demo.old", Name = "旧插件", Version = "1.0.0.0", LatestVersion = "1.1.0.0", IsUpdateAvailable = true },
                new SoftwarePackageInfo { Id = "demo.fresh", Name = "新插件", Version = "2.0.0.0", LatestVersion = "2.0.0.0" },
            ],
        }));
        var peers = factory.Services.GetRequiredService<PeerRegistry>();
        await WaitUntilAsync(() => Task.FromResult(peers.GetPluginDeviceSnapshots().Any(x => x.SoftwareInventory?.Plugins.Count == 2)));

        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await LoginWebUiAsync(browser);
        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Control"));
        var dialog = Regex.Match(html, "data-batch-settings=\"UpgradePlugins\"[\\s\\S]*?</dialog>").Value;

        Assert.Contains("可更新的插件（1）", dialog);
        Assert.Matches("name=\"PluginIds\" value=\"demo.old\" checked", dialog);
        Assert.Contains("1.0.0.0 → 1.1.0.0", dialog);
        Assert.DoesNotMatch("value=\"demo.fresh\" checked", dialog);
        Assert.Contains("ServerPreDownload", dialog);
    }

    // ---------- 辅助 ----------

    private static async Task<JsonElement> UploadAsync(HttpClient browser, string token, string fileName, byte[] content)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/BatchControl?handler=UploadPluginPackage") { Content = form };
        request.Headers.Add("X-CSRF-TOKEN", token);
        var response = await browser.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static byte[] Cipx(string id, string name, string version)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("manifest.yml").Open(), new UTF8Encoding(false)))
                writer.Write($"entranceAssembly: Demo.dll\nid: {id}\nname: {name}\nversion: {version}\napiVersion: 2.0.0.0\ndependencies: []\n");
            using (var dll = archive.CreateEntry("Demo.dll").Open()) dll.Write(RandomNumberGenerator.GetBytes(256));
        }
        return buffer.ToArray();
    }

    private static object Item(
        string id, string name, string version, string apiVersion, long downloads = 0, long stars = 0,
        (string Id, bool Required)[]? dependencies = null) => new
    {
        DownloadUrl = $"{{root}}/o/{id}/{id}.cipx",
        DownloadMd5 = "00",
        Manifest = new
        {
            Id = id,
            Name = name,
            Version = version,
            ApiVersion = apiVersion,
            Author = "author",
            Description = "description",
            Dependencies = (dependencies ?? []).Select(x => new { x.Id, IsRequired = x.Required }),
        },
        RealIconPath = $"{{root}}/o/{id}/icon.png",
        DownloadCount = downloads,
        StarsCount = stars,
    };

    private static MemoryStream IndexZip(params object[] plugins)
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        using (var stream = archive.CreateEntry("index.v2.json").Open())
            JsonSerializer.Serialize(stream, new
            {
                Plugins = plugins,
                DownloadMirrors = new Dictionary<string, string>
                {
                    ["github"] = "https://github.com",
                    ["ghproxy"] = "https://mirror.example/https://github.com",
                },
            });
        buffer.Position = 0;
        return buffer;
    }

    private static FileTransferService CreateTransfers(string root) => new(
        Options.Create(new ServerOptions { DatabasePath = Path.Combine(root, "remoteci.db") }),
        new TestHostEnvironment(root),
        TimeProvider.System,
        NullLogger<FileTransferService>.Instance);

    private sealed class TestHostEnvironment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "RemoteCI.Server.Tests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            return Task.FromResult(respond(request));
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static async Task LoginWebUiAsync(HttpClient browser)
    {
        var html = await browser.GetStringAsync("/Login");
        var token = Regex.Match(html, "<input[^>]+name=\"__RequestVerificationToken\"[^>]+value=\"([^\"]+)\"").Groups[1].Value;
        var response = await browser.PostAsync("/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Input.Username"] = TestWebApplicationFactory.AdminUsername,
            ["Input.Password"] = TestWebApplicationFactory.AdminPassword,
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token),
        }));
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }

    private static async Task<WebSocket> ConnectPluginAsync(TestWebApplicationFactory factory)
    {
        var token = await factory.GetPluginTokenAsync();
        var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(token)}"), CancellationToken.None);
        await ReceiveEnvelopeAsync(socket, Protocol.MessageTypeSchedulePull);
        await SendAsync(socket, Envelope.PeerCapabilities(new PeerCapabilities { Capabilities = [.. RemoteCiCapabilities.Current] }));
        var peers = factory.Services.GetRequiredService<PeerRegistry>();
        await WaitUntilAsync(() => Task.FromResult(
            peers.PrimaryPluginSupports(TestWebApplicationFactory.DefaultClassId, RemoteCiCapabilities.PluginPackageInstall)));
        return socket;
    }

    private static async Task SendAsync(WebSocket socket, Envelope envelope) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options),
            WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await condition())
            await Task.Delay(20, timeout.Token);
    }

    private static async Task<Envelope> ReceiveEnvelopeAsync(WebSocket socket, string expectedType)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var buffer = new byte[256 * 1024];
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var envelope = JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(message.ToArray()), JsonDefaults.Options)!;
            if (envelope.Type == expectedType) return envelope;
        }
    }
}
