using System.Net;
using System.Net.Http;
using System.Net.WebSockets;

namespace RemoteCI.Plugin.Services;

/// <summary>CloudClient 对 WebSocket 的最小抽象，便于测试注入假连接。</summary>
internal interface ICloudSocket : IDisposable
{
    WebSocketState State { get; }
    WebSocketCloseStatus? CloseStatus { get; }
    TimeSpan KeepAliveInterval { set; }
    Task ConnectAsync(Uri uri, CancellationToken ct);
    Task SendAsync(byte[] buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct);
    Task<WebSocketReceiveResult> ReceiveAsync(byte[] buffer, CancellationToken ct);
}

/// <summary>
/// 生产环境实现：包装 ClientWebSocket，并保留握手响应状态以区分鉴权失败与普通代理错误。
/// </summary>
internal sealed class ClientWebSocketAdapter : ICloudSocket
{
    private readonly ClientWebSocket _socket;
    private readonly HandshakeResponseHandler _handshakeHandler;
    private readonly HttpMessageInvoker _httpInvoker;

    public ClientWebSocketAdapter(ClientWebSocket socket)
    {
        _socket = socket;
        _handshakeHandler = new HandshakeResponseHandler(new HttpClientHandler());
        _httpInvoker = new HttpMessageInvoker(_handshakeHandler, disposeHandler: true);
    }

    public WebSocketState State => _socket.State;
    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;
    public TimeSpan KeepAliveInterval { set => _socket.Options.KeepAliveInterval = value; }

    public async Task ConnectAsync(Uri uri, CancellationToken ct)
    {
        _handshakeHandler.Reset();
        try
        {
            await _socket.ConnectAsync(uri, _httpInvoker, ct);
        }
        catch (WebSocketException ex) when (
            _handshakeHandler.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            // 握手尚未升级为 WebSocket 时无法收到协议层 Close 帧，必须从 HTTP 状态识别凭据失效。
            throw new PluginAuthenticationException(ex);
        }
    }

    public Task SendAsync(byte[] buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken ct) =>
        _socket.SendAsync(new ArraySegment<byte>(buffer), messageType, endOfMessage, ct);
    public Task<WebSocketReceiveResult> ReceiveAsync(byte[] buffer, CancellationToken ct) =>
        _socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

    public void Dispose()
    {
        _socket.Dispose();
        _httpInvoker.Dispose();
    }

    private sealed class HandshakeResponseHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        public HttpStatusCode? StatusCode { get; private set; }

        public void Reset() => StatusCode = null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            StatusCode = response.StatusCode;
            return response;
        }
    }
}
