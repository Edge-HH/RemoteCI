using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>文件中转：上传、按班级发送、插件凭下载凭据续传下载、进度回报与权限。每个测试独立服务实例。</summary>
public sealed class FileTransferTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task UploadSendDownloadAndProgress_RoundTrip()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        var content = RandomBytes(300_000);

        var uploaded = await UploadAsync(client, admin.AccessToken, "课件 第1课.pptx", content);
        Assert.Equal("课件 第1课.pptx", uploaded.FileName);
        Assert.Equal(content.Length, uploaded.Size);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(content)), uploaded.Sha256);

        using var plugin = await ConnectPluginAsync(factory, RemoteCiCapabilities.Current);
        var send = client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, $"/api/files/{uploaded.Id}/send",
            admin.AccessToken, new { classNames = new[] { TestWebApplicationFactory.DefaultClassName }, targetFolder = 1 }));
        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        var command = ConvertPayload<CommandMessage>(forwarded.Payload);
        Assert.Equal(CommandKind.SendFile, command.Command);
        var request = command.FileDistribution!;
        Assert.Null(FileDistributionRequest.ValidateTransfer(request));
        Assert.Equal(uploaded.Id, request.TransferId);
        Assert.Empty(request.ContentBase64);
        await ReplyAsync(plugin, forwarded, new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "开始接收" });
        var afterSend = await ReadAsync<FileTransferView>(await send);
        Assert.Equal(FileTransferState.Downloading, Assert.Single(afterSend.Deliveries).State);

        // 续传：从第 100000 字节开始请求，服务端按 Range 返回 206 与剩余内容。
        var pluginToken = await factory.GetPluginTokenAsync();
        using var ranged = new HttpRequestMessage(HttpMethod.Get, $"/api/plugin/files/{uploaded.Id}");
        ranged.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pluginToken);
        ranged.Headers.Add(FileTransferEndpoints.TransferKeyHeader, request.DownloadKey);
        ranged.Headers.Range = new RangeHeaderValue(100_000, null);
        var partial = await client.SendAsync(ranged);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal(content[100_000..], await partial.Content.ReadAsByteArrayAsync());

        await SendAsync(plugin, Envelope.FileTransferProgress(new FileTransferProgress
        {
            TransferId = uploaded.Id, State = FileTransferState.Downloading, ReceivedBytes = 150_000, TotalBytes = content.Length,
        }));
        await WaitUntilAsync(async () => (await GetAsync(client, admin.AccessToken, uploaded.Id)).Deliveries[0].Percent == 50);

        await SendAsync(plugin, Envelope.FileTransferProgress(new FileTransferProgress
        {
            TransferId = uploaded.Id, State = FileTransferState.Completed, ReceivedBytes = content.Length,
            TotalBytes = content.Length, SavedPath = @"C:\Users\Teacher\Desktop\课件 第1课.pptx",
        }));
        await WaitUntilAsync(async () => (await GetAsync(client, admin.AccessToken, uploaded.Id)).Deliveries[0].State == FileTransferState.Completed);
        var final = Assert.Single((await GetAsync(client, admin.AccessToken, uploaded.Id)).Deliveries);
        Assert.Equal(100, final.Percent);
        Assert.Equal(@"C:\Users\Teacher\Desktop\课件 第1课.pptx", final.SavedPath);

        // 迟到的下载中进度不能覆盖最终状态。
        await SendAsync(plugin, Envelope.FileTransferProgress(new FileTransferProgress
        {
            TransferId = uploaded.Id, State = FileTransferState.Downloading, ReceivedBytes = 10, TotalBytes = content.Length,
        }));
        await Task.Delay(200);
        Assert.Equal(FileTransferState.Completed, (await GetAsync(client, admin.AccessToken, uploaded.Id)).Deliveries[0].State);
    }

    [Fact]
    public async Task PluginDownload_RequiresRegisteredPluginAndDownloadKey()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        var uploaded = await UploadAsync(client, admin.AccessToken, "a.txt", Encoding.UTF8.GetBytes("hello"));
        var pluginToken = await factory.GetPluginTokenAsync();
        var key = factory.Services.GetRequiredService<FileTransferService>().Get(uploaded.Id)!.DownloadKey;

        // 尚未发送给任何设备：即使凭据正确也不能下载。
        Assert.Equal(HttpStatusCode.NotFound, (await DownloadAsync(client, pluginToken, uploaded.Id, key)).StatusCode);

        using var plugin = await ConnectPluginAsync(factory, RemoteCiCapabilities.Current);
        var send = client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, $"/api/files/{uploaded.Id}/send",
            admin.AccessToken, new { classIds = new[] { TestWebApplicationFactory.DefaultClassId } }));
        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        await ReplyAsync(plugin, forwarded, new CommandResult { Success = true, Code = CommandResultCodes.Ok });
        (await send).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NotFound, (await DownloadAsync(client, pluginToken, uploaded.Id, "wrong-key-0123456789")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await DownloadAsync(client, admin.AccessToken, uploaded.Id, key)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await DownloadAsync(client, "invalid", uploaded.Id, key)).StatusCode);
        var ok = await DownloadAsync(client, pluginToken, uploaded.Id, key);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("hello", await ok.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SendFiles_PermissionGatesUploadAndSend()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        var plain = await CreateUserAsync(client, admin.AccessToken, "file.plain", UserPermissions.None);
        var allowed = await CreateUserAsync(client, admin.AccessToken, "file.allowed", UserPermissions.SendFiles);

        var denied = await client.SendAsync(UploadRequest(plain.AccessToken, "x.txt", [1, 2, 3]));
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var uploaded = await UploadAsync(client, allowed.AccessToken, "x.txt", [1, 2, 3]);
        // 插件离线：发送请求本身成功，逐班结果标明失败原因。
        var sent = await ReadAsync<FileTransferView>(await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, $"/api/files/{uploaded.Id}/send", allowed.AccessToken,
            new { classNames = new[] { "测试" } })));
        var delivery = Assert.Single(sent.Deliveries);
        Assert.Equal(FileTransferState.Failed, delivery.State);
        Assert.Contains("未在线", delivery.Message);

        // 别人的传输对其他普通账号不可见。
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Get, $"/api/files/{uploaded.Id}", plain.AccessToken))).StatusCode);
        var unknown = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, $"/api/files/{uploaded.Id}/send", allowed.AccessToken, new { classNames = new[] { "不存在的班" } }));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task OldPlugin_AdminSmallFileFallsBackToInline()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        var uploaded = await UploadAsync(client, admin.AccessToken, "old.txt", Encoding.UTF8.GetBytes("legacy"));
        var oldCapabilities = RemoteCiCapabilities.Current.Where(x => x != RemoteCiCapabilities.FileTransfer).ToList();
        using var plugin = await ConnectPluginAsync(factory, oldCapabilities);

        var send = client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, $"/api/files/{uploaded.Id}/send",
            admin.AccessToken, new { classIds = new[] { TestWebApplicationFactory.DefaultClassId } }));
        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        var request = ConvertPayload<CommandMessage>(forwarded.Payload).FileDistribution!;
        Assert.False(request.IsTransfer);
        Assert.True(FileDistributionRequest.TryDecode(request, out var inline));
        Assert.Equal("legacy", Encoding.UTF8.GetString(inline));
        await ReplyAsync(plugin, forwarded, new CommandResult { Success = true, Code = CommandResultCodes.Ok, Data = @"D:\Desktop\old.txt" });
        var delivery = Assert.Single((await ReadAsync<FileTransferView>(await send)).Deliveries);
        Assert.Equal(FileTransferState.Completed, delivery.State);
        Assert.Equal(@"D:\Desktop\old.txt", delivery.SavedPath);
    }

    [Fact]
    public async Task GenericCommandApi_RejectsTransferModeAndNonAdminInline()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        var transfer = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/commands", admin.AccessToken,
            new CommandMessage
            {
                Command = CommandKind.SendFile,
                ClassId = TestWebApplicationFactory.DefaultClassId,
                FileDistribution = new FileDistributionRequest { FileName = "a.txt", TransferId = new string('a', 32) },
            }));
        Assert.Equal(HttpStatusCode.BadRequest, transfer.StatusCode);

        var teacher = await CreateUserAsync(client, admin.AccessToken, "file.inline", UserPermissions.SendFiles);
        var inline = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/commands", teacher.AccessToken,
            new CommandMessage
            {
                Command = CommandKind.SendFile,
                ClassId = TestWebApplicationFactory.DefaultClassId,
                FileDistribution = new FileDistributionRequest { FileName = "a.txt", ContentBase64 = "aGk=" },
            }));
        Assert.Equal(HttpStatusCode.Forbidden, inline.StatusCode);
    }

    [Fact]
    public async Task Upload_RejectsFilesAboveConfiguredLimit()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value.FileTransferMaxBytes = 1024;
        var response = await client.SendAsync(UploadRequest(admin.AccessToken, "big.bin", new byte[2048]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(Directory.EnumerateFiles(factory.Services.GetRequiredService<FileTransferService>().Directory, "*.bin"));
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(7).NextBytes(bytes);
        return bytes;
    }

    private static HttpRequestMessage UploadRequest(string token, string name, byte[] content)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/files?name={Uri.EscapeDataString(name)}")
        {
            Content = new ByteArrayContent(content),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<FileTransferView> UploadAsync(HttpClient client, string token, string name, byte[] content)
    {
        var response = await client.SendAsync(UploadRequest(token, name, content));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<FileTransferView>(response);
    }

    private static async Task<HttpResponseMessage> DownloadAsync(HttpClient client, string token, string id, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/plugin/files/{id}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(FileTransferEndpoints.TransferKeyHeader, key);
        return await client.SendAsync(request);
    }

    private static async Task<FileTransferView> GetAsync(HttpClient client, string token, string id) =>
        await ReadAsync<FileTransferView>(await client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, $"/api/files/{id}", token)));

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, Json)!;
    }

    private static async Task<AuthResponse> CreateUserAsync(
        HttpClient client, string adminToken, string username, UserPermissions granted)
    {
        (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", adminToken, new CreateUserRequest
        {
            ClassId = TestWebApplicationFactory.DefaultClassId,
            Username = username,
            DisplayName = username,
            Password = "File-Password-2026",
            Role = UserRole.User,
            GrantedPermissions = granted | UserPermissions.ApiAccess,
        }))).EnsureSuccessStatusCode();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = username, Password = "File-Password-2026", DeviceName = "Integration Test",
        });
        login.EnsureSuccessStatusCode();
        return (await login.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    private static async Task<WebSocket> ConnectPluginAsync(TestWebApplicationFactory factory, IReadOnlyList<string> capabilities)
    {
        var token = await factory.GetPluginTokenAsync();
        var socket = await factory.Server.CreateWebSocketClient().ConnectAsync(
            new Uri(factory.Server.BaseAddress, $"/ws?{Protocol.QueryToken}={Uri.EscapeDataString(token)}"), CancellationToken.None);
        await ReceiveEnvelopeAsync(socket, Protocol.MessageTypeSchedulePull);
        await SendAsync(socket, Envelope.PeerCapabilities(new PeerCapabilities { Capabilities = [.. capabilities] }));
        var peers = factory.Services.GetRequiredService<PeerRegistry>();
        var probe = capabilities.Contains(RemoteCiCapabilities.FileTransfer)
            ? RemoteCiCapabilities.FileTransfer
            : RemoteCiCapabilities.FileDistribute;
        await WaitUntilAsync(() => Task.FromResult(peers.PrimaryPluginSupports(TestWebApplicationFactory.DefaultClassId, probe)));
        return socket;
    }

    private static Task ReplyAsync(WebSocket plugin, Envelope forwarded, CommandResult result) => SendAsync(plugin, new Envelope
    {
        Type = Protocol.MessageTypeCommandResult,
        ReplyToMessageId = forwarded.MessageId,
        Payload = result,
    });

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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
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

    private static T ConvertPayload<T>(object? payload) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(payload), JsonDefaults.Options)!;
}
