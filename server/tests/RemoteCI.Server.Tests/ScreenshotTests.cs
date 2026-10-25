using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

/// <summary>屏幕截图：下发 CaptureScreen、插件凭上传凭据上传 PNG、按权限查看。每个测试独立服务实例。</summary>
public sealed class ScreenshotTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CaptureUploadAndView_RoundTrip()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        using var plugin = await ConnectPluginAsync(factory, RemoteCiCapabilities.Current);
        var pluginToken = await factory.GetPluginTokenAsync();
        var png = Png(1920, 1080);

        var capture = RequestCaptureAsync(client, admin.AccessToken);
        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        var command = ConvertPayload<CommandMessage>(forwarded.Payload);
        Assert.Equal(CommandKind.CaptureScreen, command.Command);
        var request = command.Screenshot!;
        Assert.Null(ScreenshotRequest.Validate(request));
        Assert.False(request.Notify);
        Assert.True(command.RequestedBy!.Permissions.HasFlag(UserPermissions.CaptureScreen));

        Assert.Equal(HttpStatusCode.NoContent, (await UploadAsync(client, pluginToken, request.Id, request.UploadKey, png)).StatusCode);
        await ReplyAsync(plugin, forwarded, new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "截图已上传" });

        var response = await capture;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var view = await ReadAsync<ScreenshotView>(response);
        Assert.Equal(request.Id, view.Id);
        Assert.Equal((1920, 1080), (view.Width, view.Height));
        Assert.Equal(png.Length, view.Size);

        var image = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, view.Url, admin.AccessToken));
        Assert.Equal("image/png", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(png, await image.Content.ReadAsByteArrayAsync());
        var list = await ReadAsync<List<ScreenshotView>>(await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Get, $"/api/classes/{TestWebApplicationFactory.DefaultClassId}/screenshots", admin.AccessToken)));
        Assert.Equal(view.Id, Assert.Single(list).Id);

        // 同一记录只接受一次上传。
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(client, pluginToken, request.Id, request.UploadKey, png)).StatusCode);
    }

    [Fact]
    public async Task PluginUpload_RequiresPluginTokenKeyAndPng()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        using var plugin = await ConnectPluginAsync(factory, RemoteCiCapabilities.Current);
        var pluginToken = await factory.GetPluginTokenAsync();

        var capture = RequestCaptureAsync(client, admin.AccessToken);
        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        var request = ConvertPayload<CommandMessage>(forwarded.Payload).Screenshot!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await UploadAsync(client, null, request.Id, request.UploadKey, Png(10, 10))).StatusCode);
        // 用户访问令牌不是插件凭据，即使上传凭据正确也拒绝。
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(client, admin.AccessToken, request.Id, request.UploadKey, Png(10, 10))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await UploadAsync(client, pluginToken, request.Id, "wrong-key", Png(10, 10))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, pluginToken, request.Id, request.UploadKey,
            Encoding.UTF8.GetBytes("not a png file, definitely not a png"))).StatusCode);

        // 插件回执成功但没有上传：接口返回失败，不留下空记录。
        await ReplyAsync(plugin, forwarded, new CommandResult { Success = true, Code = CommandResultCodes.Ok, Message = "截图已上传" });
        var response = await capture;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Null(factory.Services.GetRequiredService<ScreenshotService>().Get(request.Id));
    }

    [Fact]
    public async Task Permissions_ClassAdministratorByDefault_TeacherNeedsGrant()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        using var plugin = await ConnectPluginAsync(factory, RemoteCiCapabilities.Current);

        var teacher = await CreateUserAsync(client, admin.AccessToken, "shot.teacher", AccountRole.TeacherId, UserPermissions.None);
        Assert.Equal(HttpStatusCode.Forbidden, (await RequestCaptureAsync(client, teacher.AccessToken)).StatusCode);
        // 按设备截图仅限系统管理员。
        var granted = await CreateUserAsync(client, admin.AccessToken, "shot.granted", AccountRole.TeacherId, UserPermissions.CaptureScreen);
        Assert.Equal(HttpStatusCode.Forbidden, (await RequestCaptureAsync(client, granted.AccessToken, Guid.NewGuid())).StatusCode);

        var classAdmin = await CreateUserAsync(client, admin.AccessToken, "shot.head", AccountRole.ClassAdministratorId, UserPermissions.None);
        var capture = RequestCaptureAsync(client, classAdmin.AccessToken);
        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        Assert.Equal(CommandKind.CaptureScreen, ConvertPayload<CommandMessage>(forwarded.Payload).Command);
        await ReplyAsync(plugin, forwarded, CommandResult.Failure(CommandResultCodes.InternalError, "无法截取屏幕"));
        var failed = await capture;
        Assert.Equal(HttpStatusCode.UnprocessableEntity, failed.StatusCode);
        Assert.Equal("无法截取屏幕", (await failed.Content.ReadFromJsonAsync<CommandResult>(Json))!.Message);

        // 通用命令接口不能绕过截图入口（没有上传凭据）。
        var generic = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post,
            $"/api/commands?classId={TestWebApplicationFactory.DefaultClassId}", admin.AccessToken,
            new CommandMessage { Command = CommandKind.CaptureScreen }));
        Assert.False(generic.IsSuccessStatusCode);
    }

    [Fact]
    public async Task OldPlugin_IsReportedAsUnsupported_AndNotifySettingIsForwarded()
    {
        await using var factory = new TestWebApplicationFactory();
        var client = factory.CreateClient();
        var admin = await factory.LoginAsync();
        using (var oldPlugin = await ConnectPluginAsync(factory,
                   RemoteCiCapabilities.Current.Where(x => x != RemoteCiCapabilities.ScreenCapture).ToList()))
        {
            Assert.Equal(HttpStatusCode.Conflict, (await RequestCaptureAsync(client, admin.AccessToken)).StatusCode);
            await oldPlugin.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        var peers = factory.Services.GetRequiredService<PeerRegistry>();
        await WaitUntilAsync(() => Task.FromResult(!peers.HasPluginFor(TestWebApplicationFactory.DefaultClassId)));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.SystemMetadata.Where(x => x.Id == 1)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ScreenshotNotifyClassroom, true));
        }
        using var plugin = await ConnectPluginAsync(factory, RemoteCiCapabilities.Current);
        var capture = RequestCaptureAsync(client, admin.AccessToken);
        var forwarded = await ReceiveEnvelopeAsync(plugin, Protocol.MessageTypeCommand);
        Assert.True(ConvertPayload<CommandMessage>(forwarded.Payload).Screenshot!.Notify);
        await ReplyAsync(plugin, forwarded, CommandResult.Failure(CommandResultCodes.InternalError, "无法截取屏幕"));
        await capture;
    }

    [Fact]
    public void Cleanup_RemovesExpiredScreenshots()
    {
        var time = new ManualTime(DateTimeOffset.UtcNow);
        var root = Path.Combine(Path.GetTempPath(), "remoteci-shots-" + Guid.NewGuid().ToString("N"));
        try
        {
            var service = new ScreenshotService(
                Microsoft.Extensions.Options.Options.Create(new ServerOptions { DatabasePath = Path.Combine(root, "remoteci.db") }),
                new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = root }, time,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ScreenshotService>.Instance);
            var record = service.CreatePending(Guid.NewGuid(), Guid.NewGuid(), "教室电脑", new UserProfile { DisplayName = "管理员" });
            service.StoreAsync(record, new MemoryStream(Png(4, 3))).GetAwaiter().GetResult();
            Assert.NotNull(service.Get(record.Id));

            time.Advance(service.Retention + TimeSpan.FromMinutes(1));
            Assert.Equal(1, service.Cleanup());
            Assert.False(File.Exists(service.ContentPath(record.Id)));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    /// <summary>只含签名与 IHDR 的 PNG 头；服务端只校验签名并读取宽高。</summary>
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13);
        Encoding.ASCII.GetBytes("IHDR").CopyTo(bytes, 12);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private static Task<HttpResponseMessage> RequestCaptureAsync(HttpClient client, string token, Guid? credentialId = null) =>
        client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post,
            $"/api/classes/{TestWebApplicationFactory.DefaultClassId}/screenshot", token, new { credentialId }));

    private static Task<HttpResponseMessage> UploadAsync(HttpClient client, string? token, string id, string key, byte[] png)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/plugin/screenshots/{id}")
        {
            Content = new ByteArrayContent(png),
        };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add(ScreenshotRequest.UploadKeyHeader, key);
        return client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonSerializer.Deserialize<T>(text, Json)!;
    }

    private static async Task<AuthResponse> CreateUserAsync(
        HttpClient client, string adminToken, string username, Guid roleId, UserPermissions granted)
    {
        (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", adminToken, new CreateUserRequest
        {
            ClassId = TestWebApplicationFactory.DefaultClassId,
            Username = username,
            DisplayName = username,
            Password = "Shot-Password-2026",
            Role = UserRole.User,
            RoleId = roleId,
            GrantedPermissions = granted | UserPermissions.ApiAccess,
        }))).EnsureSuccessStatusCode();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = username, Password = "Shot-Password-2026", DeviceName = "Integration Test",
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
        await WaitUntilAsync(() => Task.FromResult(
            peers.PrimaryPluginSupports(TestWebApplicationFactory.DefaultClassId, RemoteCiCapabilities.FileTransfer)));
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

    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
