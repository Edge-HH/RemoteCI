using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Tests;

/// <summary>独立测试类模拟插件连接时共用的收发帮助方法。</summary>
internal static class TestSockets
{
    public static async Task SendAsync(WebSocket socket, Envelope envelope) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(envelope, JsonDefaults.Options),
            WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>读取直到收到指定类型的消息，跳过课表拉取、能力同步等其他消息。</summary>
    public static async Task<Envelope> ReceiveAsync(WebSocket socket, string type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[256 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, timeout.Token);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var envelope = JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(message.ToArray()), JsonDefaults.Options)!;
            if (envelope.Type == type) return envelope;
        }
    }
}
