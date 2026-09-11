using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Plugin.Services;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class CloudClientTests
{
    private static ScheduleSyncStatus RunningStatus(ScheduleSyncRequest request) => new()
    {
        TaskId = request.TaskId,
        Source = request.Source,
        State = ScheduleSyncTaskState.Running,
    };

    private sealed class FakeSocket : ICloudSocket
    {
        public WebSocketState State { get; set; } = WebSocketState.Open;
        public WebSocketCloseStatus? CloseStatus => null;
        public TimeSpan KeepAliveInterval { get; set; }
        public int ConnectCount { get; private set; }
        public int DisposeCount { get; private set; }
        public WebSocketReceiveResult? NextReceive { get; set; }
        public Exception? ReceiveError { get; set; }
        public Exception? SendError { get; set; }
        public Exception? ConnectError { get; set; }
        public TimeSpan SendDelay { get; set; }
        public ConcurrentQueue<byte[]> SentMessages { get; } = new();

        public Task ConnectAsync(Uri uri, CancellationToken ct)
        {
            ConnectCount++;
            return ConnectError is null ? Task.CompletedTask : Task.FromException(ConnectError);
        }

        public async Task SendAsync(byte[] buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct)
        {
            if (SendError is not null) throw SendError;
            if (SendDelay > TimeSpan.Zero) await Task.Delay(SendDelay, ct);
            SentMessages.Enqueue(buffer.ToArray());
        }

        public async Task<WebSocketReceiveResult> ReceiveAsync(byte[] buffer, CancellationToken ct)
        {
            if (ReceiveError is not null) throw ReceiveError;
            if (NextReceive is not null) return NextReceive;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct); // 模拟健康空闲连接，阻塞到取消。
            throw new OperationCanceledException(ct);
        }

        public void Dispose()
        {
            DisposeCount++;
            State = WebSocketState.Closed;
        }
    }

    private static (CloudClient Client, List<FakeSocket> Sockets) Create(
        PluginSettings? settings = null,
        TimeSpan? reconnectDelay = null,
        CloudTokenStore? tokenStore = null,
        HttpMessageHandler? httpHandler = null,
        params FakeSocket[] preSeeded)
    {
        var sockets = new List<FakeSocket>(preSeeded);
        var next = 0;
        var mirror = new AccountMirror(Path.Combine(
            Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "accounts.json"));
        var client = new CloudClient(
            settings ?? new PluginSettings { CloudToken = "test-token", EnableCloud = true },
            mirror,
            commands: null,
            requestScheduleSync: request => RunningStatus(request),
            NullLogger<CloudClient>.Instance,
            tokenStore: tokenStore,
            socketFactory: () =>
            {
                if (next < sockets.Count) return sockets[next++];
                var fresh = new FakeSocket();
                sockets.Add(fresh);
                next++;
                return fresh;
            },
            httpHandler: httpHandler,
            reconnectDelay: reconnectDelay ?? TimeSpan.FromMilliseconds(20));
        return (client, sockets);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail("条件在超时前未满足");
    }

    [Fact]
    public async Task ConnectedHook_RepublishesExtensionSnapshotAfterSocketIsReady()
    {
        var socket = new FakeSocket();
        var (client, _) = Create(preSeeded: socket);
        client.Connected += (_, _) => _ = client.SendExtensionsAsync(
        [
            new ExtensionDefinition { Id = "demo.connected", DisplayName = "Connected extension" },
        ]);
        try
        {
            _ = client.StartAsync();
            await WaitUntilAsync(() => socket.SentMessages.Any(bytes =>
                JsonSerializer.Deserialize<Envelope>(bytes, JsonDefaults.Options)?.Type == Protocol.MessageTypeExtensionsSync), 2000);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task Connect_ReportsSoftwareVersionAndCapabilities()
    {
        var socket = new FakeSocket();
        var (client, _) = Create(preSeeded: socket);
        try
        {
            _ = client.StartAsync();
            await WaitUntilAsync(() => socket.SentMessages.Any(bytes =>
                JsonSerializer.Deserialize<Envelope>(bytes, JsonDefaults.Options)?.Type ==
                Protocol.MessageTypePeerCapabilities), 2000);

            var envelope = socket.SentMessages
                .Select(bytes => JsonSerializer.Deserialize<Envelope>(bytes, JsonDefaults.Options)!)
                .First(value => value.Type == Protocol.MessageTypePeerCapabilities);
            var report = JsonSerializer.Deserialize<PeerCapabilities>(
                JsonSerializer.Serialize(envelope.Payload), JsonDefaults.Options)!;
            Assert.Equal("3.2.1.3", report.SoftwareVersion);
            Assert.Contains(RemoteCiCapabilities.ScheduleChange, report.Capabilities);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task ReceiveFailure_ReleasesSocketAndReconnects()
    {
        var failing = new FakeSocket { ReceiveError = new WebSocketException("connection lost") };
        var (client, sockets) = Create(preSeeded: failing);
        using var cts = new CancellationTokenSource();
        try
        {
            _ = client.StartAsync(cts.Token);

            // 第一个连接接收失败后应释放并自动重连到第二个连接。
            await WaitUntilAsync(
                () => sockets.Count >= 2 && sockets[1].ConnectCount == 1 && failing.DisposeCount >= 1, 2000);

            Assert.True(sockets[0].KeepAliveInterval > TimeSpan.Zero, "每个连接都应配置 KeepAlive");
        }
        finally
        {
            cts.Cancel();
            client.Dispose();
        }
    }

    [Fact]
    public async Task ConnectionStatus_ReportsConnectedAndSanitizedFailure()
    {
        var failing = new FakeSocket { ReceiveError = new WebSocketException("connection lost") };
        var (client, _) = Create(preSeeded: failing);
        var statuses = new ConcurrentQueue<CloudConnectionStatus>();
        client.ConnectionStatusChanged += statuses.Enqueue;
        try
        {
            _ = client.StartAsync();
            await WaitUntilAsync(
                () => statuses.Any(status => status.State == CloudConnectionState.WaitingToRetry), 2000);

            Assert.Contains(statuses, status => status.State == CloudConnectionState.Connected);
            var failed = statuses.First(status => status.State == CloudConnectionState.WaitingToRetry);
            Assert.Contains("connection lost", failed.Error);
            Assert.DoesNotContain("test-token", failed.Error);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task ConnectionStatus_RedactsCredentialFromHandshakeError()
    {
        const string secret = "secret-token";
        var failing = new FakeSocket
        {
            ConnectError = new WebSocketException($"connect wss://server/ws?token={secret} failed"),
        };
        var settings = new PluginSettings { CloudToken = secret, EnableCloud = true };
        var (client, _) = Create(settings, preSeeded: failing);
        try
        {
            _ = client.StartAsync();
            await WaitUntilAsync(
                () => client.CurrentStatus.State == CloudConnectionState.WaitingToRetry, 2000);

            Assert.DoesNotContain(secret, client.CurrentStatus.Error);
            Assert.Contains("token=***", client.CurrentStatus.Error);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Theory]
    [InlineData(401, true)]
    [InlineData(403, true)]
    [InlineData(404, false)]
    public async Task ClientWebSocketAdapter_ClassifiesOnlyAuthenticationHandshakeFailures(
        int statusCode,
        bool shouldBeAuthenticationFailure)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var request = new byte[4096];
            _ = await stream.ReadAsync(request);
            var reason = statusCode switch
            {
                401 => "Unauthorized",
                403 => "Forbidden",
                _ => "Not Found",
            };
            var response = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {statusCode} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response);
        });

        using var adapter = new ClientWebSocketAdapter(new ClientWebSocket());
        var error = await Record.ExceptionAsync(() => adapter.ConnectAsync(
            new Uri($"ws://127.0.0.1:{port}/ws?token=%3CREDACTED%3E"),
            CancellationToken.None));
        await server;

        Assert.NotNull(error);
        Assert.Equal(shouldBeAuthenticationFailure, error.GetType().Name == "PluginAuthenticationException");
        if (!shouldBeAuthenticationFailure) Assert.IsType<WebSocketException>(error);
    }

    [Fact]
    public async Task ClientWebSocketAdapter_StillCompletesSuccessfulUpgrade()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var releaseServer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var request = new byte[4096];
            var count = await stream.ReadAsync(request);
            var requestText = Encoding.ASCII.GetString(request, 0, count);
            var webSocketKey = requestText
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
                .Split(':', 2)[1]
                .Trim();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
                webSocketKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
            await stream.WriteAsync(response);
            await releaseServer.Task;
        });

        using var adapter = new ClientWebSocketAdapter(new ClientWebSocket());
        try
        {
            adapter.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await adapter.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), CancellationToken.None);
            Assert.Equal(WebSocketState.Open, adapter.State);
        }
        finally
        {
            releaseServer.TrySetResult();
            await server;
        }
    }

    [Fact]
    public async Task HandshakeAuthenticationFailure_ClearsOldTokenAndUsesSavedPairCode()
    {
        var settings = new PluginSettings
        {
            CloudToken = "stale-token",
            PluginPairCode = "fresh-pair-code",
            EnableCloud = true,
        };
        var storePath = Path.Combine(
            Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "token.bin");
        var store = new CloudTokenStore(storePath);
        store.Save("stale-token");
        var pairRequests = 0;
        var handler = new StubHandler(_ =>
        {
            Interlocked.Increment(ref pairRequests);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"token":"replacement-token","role":"plugin"}""",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var rejected = new FakeSocket
        {
            ConnectError = new PluginAuthenticationException(new WebSocketException("handshake rejected")),
        };
        var connected = new FakeSocket();
        var (client, _) = Create(
            settings,
            tokenStore: store,
            httpHandler: handler,
            preSeeded: [rejected, connected]);
        try
        {
            _ = client.StartAsync();
            await WaitUntilAsync(
                () => client.CurrentStatus.IsConnected && settings.CloudToken == "replacement-token",
                3000);

            Assert.Equal(1, pairRequests);
            Assert.Equal("replacement-token", store.Load());
            Assert.Empty(settings.PluginPairCode);
            Assert.True(rejected.DisposeCount >= 1);
            Assert.Equal(1, connected.ConnectCount);
        }
        finally
        {
            client.Dispose();
            if (File.Exists(storePath)) File.Delete(storePath);
        }
    }

    [Fact]
    public async Task ManualTest_WakesCredentialWaitAndUsesRealWebSocketFlow()
    {
        var settings = new PluginSettings { EnableCloud = true };
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"token":"paired-token","role":"plugin"}""", Encoding.UTF8, "application/json"),
        });
        var socket = new FakeSocket();
        var (client, _) = Create(settings, httpHandler: handler, preSeeded: socket);
        try
        {
            _ = client.StartAsync();
            await WaitUntilAsync(
                () => client.CurrentStatus.State == CloudConnectionState.WaitingForCredentials, 2000);

            settings.PluginPairCode = "new-pair-code";
            var result = await client.TestConnectionAsync();

            Assert.True(result.Success, result.Message);
            Assert.Equal(CloudConnectionState.Connected, result.Status.State);
            Assert.Equal(1, socket.ConnectCount);
            Assert.Contains(socket.SentMessages, bytes =>
                JsonSerializer.Deserialize<Envelope>(bytes, JsonDefaults.Options)?.Type ==
                Protocol.MessageTypePluginNetworkInfo);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task ManualTest_WhenCloudDisabledExplainsRequiredAction()
    {
        var (client, _) = Create(new PluginSettings { EnableCloud = false });
        try
        {
            _ = client.StartAsync();
            var result = await client.TestConnectionAsync();

            Assert.False(result.Success);
            Assert.Equal(CloudConnectionState.Disabled, result.Status.State);
            Assert.Contains("开发者设置", result.Message);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task ManualTest_ReportsWhenSavedServerAddressNeedsRestart()
    {
        var settings = new PluginSettings
        {
            CloudToken = "test-token",
            CloudServerUrl = "https://old.example.com",
            EnableCloud = true,
        };
        var (client, _) = Create(settings, preSeeded: new FakeSocket());
        try
        {
            _ = client.StartAsync();
            await WaitUntilAsync(() => client.CurrentStatus.IsConnected, 2000);

            settings.CloudServerUrl = "https://new.example.com";
            var result = await client.TestConnectionAsync();

            Assert.True(result.Success, result.Message);
            Assert.Contains("https://old.example.com", result.Message);
            Assert.Contains("重启 ClassIsland", result.Message);
            Assert.Contains("https://old.example.com", result.Status.Summary);
            Assert.DoesNotContain("https://new.example.com", result.Status.Summary);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task PolicyViolationClose_ClearsLongTermCredential()
    {
        var settings = new PluginSettings { CloudToken = "doomed-token", EnableCloud = true };
        var policy = new FakeSocket
        {
            NextReceive = new WebSocketReceiveResult(
                0, WebSocketMessageType.Close, true, WebSocketCloseStatus.PolicyViolation, "bye"),
        };
        var storePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "token.bin");
        var store = new CloudTokenStore(storePath);
        store.Save("doomed-token");
        var (client, _) = Create(settings, tokenStore: store, preSeeded: policy);
        try
        {
            _ = client.StartAsync();
            await WaitUntilAsync(() => settings.CloudToken is null, 2000);
            // 吊销后加密存储也同步删除。
            Assert.False(File.Exists(storePath));
        }
        finally
        {
            client.Dispose();
            if (File.Exists(storePath)) File.Delete(storePath);
        }
    }

    [Fact]
    public async Task SendFailureOnOpenSocket_DisposesSocketAndReconnects()
    {
        var failing = new FakeSocket { SendError = new WebSocketException("send failed") };
        var (client, sockets) = Create(preSeeded: failing);
        try
        {
            _ = client.StartAsync();
            // 连接建立后立即发送网络信息失败 → 释放该 socket → 外层重连到新连接。
            await WaitUntilAsync(() => failing.DisposeCount >= 1 && sockets.Count >= 2, 2000);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task MissingCredentials_DoesNotAttemptConnection()
    {
        var settings = new PluginSettings { EnableCloud = true }; // 无 token、无配对码。
        var (client, sockets) = Create(settings);
        try
        {
            _ = client.StartAsync();
            await Task.Delay(150);
            Assert.Empty(sockets);
        }
        finally
        {
            client.Dispose();
        }
    }

    [Fact]
    public async Task ConcurrentSendsDuringDispose_DoNotThrow()
    {
        // 慢速 socket 制造在途发送窗口;Dispose 与并发发送交错时不得抛 ObjectDisposedException。
        var socket = new FakeSocket { SendDelay = TimeSpan.FromMilliseconds(5) };
        var (client, _) = Create(preSeeded: socket);
        var snapshot = new ClassStateSnapshot();
        var sends = Enumerable.Range(0, 30)
            .Select(_ => client.SendStateAsync(snapshot))
            .ToArray();
        await Task.Delay(2);
        client.Dispose();
        await Task.WhenAll(sends); // 全部完成即通过（在途发送不抛异常）。
    }

    [Fact]
    public async Task PairingRequest_IssuesAndPersistsLongTermCredential()
    {
        var settings = new PluginSettings { PluginPairCode = "pair-code", EnableCloud = true }; // 无长期凭据。
        var storePath = Path.Combine(Path.GetTempPath(), "RemoteCI.Plugin.Tests", Guid.NewGuid().ToString("N"), "token.bin");
        var store = new CloudTokenStore(storePath);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"token":"paired-token","role":"plugin"}""", Encoding.UTF8, "application/json"),
        });
        var (client, _) = Create(settings, tokenStore: store, httpHandler: handler, preSeeded: new FakeSocket());
        try
        {
            _ = client.StartAsync();
            await WaitUntilAsync(
                () => settings.CloudToken == "paired-token" && string.IsNullOrEmpty(settings.PluginPairCode), 3000);

            // 凭据经 Token 访问器同步写入加密存储。
            Assert.Equal("paired-token", store.Load());
        }
        finally
        {
            client.Dispose();
            if (File.Exists(storePath)) File.Delete(storePath);
        }
    }

    [Fact]
    public async Task PairingRejection_KeepsPairCodeAndDoesNotInventCredential()
    {
        var settings = new PluginSettings { PluginPairCode = "expired-code", EnableCloud = true };
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Conflict));
        var (client, _) = Create(settings, httpHandler: handler);
        try
        {
            _ = client.StartAsync();
            await Task.Delay(150);

            // 409 视为凭据失效：不签发 token，也不清空用户重新填写的配对码。
            Assert.Null(settings.CloudToken);
            Assert.Equal("expired-code", settings.PluginPairCode);
        }
        finally
        {
            client.Dispose();
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(responder(request));
    }
}
