using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;

namespace RemoteCI.Plugin.Services;

/// <summary>在固定 UDP 端口响应局域网扫描；实际 WebSocket 端口仍可由用户修改。</summary>
internal sealed class LanDiscoveryResponder(
    PluginSettings settings,
    ILogger logger) : IDisposable
{
    private CancellationTokenSource? _cts;
    private UdpClient? _udp;

    public void Start()
    {
        try
        {
            _cts = new CancellationTokenSource();
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Protocol.LanDiscoveryPort));
            _ = RunAsync(_cts.Token);
            logger.LogInformation("局域网设备扫描已启动：UDP {Port}", Protocol.LanDiscoveryPort);
        }
        catch (SocketException ex)
        {
            logger.LogWarning(ex, "无法监听局域网扫描端口 UDP {Port}，仍可手动填写插件地址", Protocol.LanDiscoveryPort);
            Dispose();
        }
    }

    /// <summary>接收回包与中断后重建都在同一个循环内，避免递归调用随故障次数无限嵌套。</summary>
    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var udp = _udp;
            if (udp is null) return;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var packet = await udp.ReceiveAsync(ct);
                    var response = LanDiscoveryProtocol.CreateResponse(packet.Buffer, settings, Environment.MachineName);
                    if (response is null) continue;
                    var payload = JsonSerializer.SerializeToUtf8Bytes(response, JsonDefaults.Options);
                    await udp.SendAsync(payload, packet.RemoteEndPoint, ct);
                }
                return;
            }
            catch (Exception ex) when ((ex is OperationCanceledException or ObjectDisposedException) && ct.IsCancellationRequested)
            {
                return; // 正常停止。
            }
            catch (ObjectDisposedException)
            {
                return; // 套接字已被 Dispose（停止流程），不再重绑。
            }
            catch (SocketException ex)
            {
                // 网卡热插拔或瞬时故障会中断监听；退避后自动重启，而不是永久失效。
                logger.LogWarning(ex, "局域网设备扫描监听已中断，10 秒后自动重启");
                if (!await DelayAsync(TimeSpan.FromSeconds(10), ct)) return;
                if (ReferenceEquals(_udp, udp)) _udp = null;
                udp.Dispose();
                try
                {
                    _udp = new UdpClient(new IPEndPoint(IPAddress.Any, Protocol.LanDiscoveryPort));
                }
                catch (SocketException rebindEx)
                {
                    logger.LogWarning(rebindEx, "局域网扫描端口重新绑定失败，10 秒后重试");
                    if (!await DelayAsync(TimeSpan.FromSeconds(10), ct)) return;
                }
            }
        }
    }

    /// <summary>可取消的延迟；返回 false 表示等待期间被取消，调用方应退出循环。</summary>
    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
            return !ct.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null;
        _cts?.Dispose();
        _cts = null;
    }
}
