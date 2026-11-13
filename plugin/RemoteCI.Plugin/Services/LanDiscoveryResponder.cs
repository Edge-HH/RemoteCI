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
    private static readonly TimeSpan RebindDelayInitial = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RebindDelayMax = TimeSpan.FromSeconds(30);

    private readonly object _udpGate = new();
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
        var rebindDelay = RebindDelayInitial;
        while (!ct.IsCancellationRequested)
        {
            var udp = _udp;
            if (udp is null)
            {
                // 重绑失败后循环保持存活：按退避在后续 tick 再次尝试绑定，而不是永久停止响应。
                if (!await DelayAsync(rebindDelay, ct)) return;
                try
                {
                    udp = new UdpClient(new IPEndPoint(IPAddress.Any, Protocol.LanDiscoveryPort));
                }
                catch (SocketException ex)
                {
                    rebindDelay = TimeSpan.FromSeconds(Math.Min(rebindDelay.TotalSeconds * 2, RebindDelayMax.TotalSeconds));
                    logger.LogWarning(ex, "局域网扫描端口重新绑定失败，{Delay} 秒后重试", rebindDelay.TotalSeconds);
                    continue;
                }
                lock (_udpGate)
                {
                    if (ct.IsCancellationRequested)
                    {
                        udp.Dispose(); // 绑定期间收到停止请求，直接释放新套接字。
                        return;
                    }
                    _udp = udp;
                }
                rebindDelay = RebindDelayInitial;
                logger.LogInformation("局域网设备扫描已恢复：UDP {Port}", Protocol.LanDiscoveryPort);
            }
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
                // 网卡热插拔或瞬时故障会中断监听；释放旧套接字后由循环按退避重新绑定。
                logger.LogWarning(ex, "局域网设备扫描监听已中断，{Delay} 秒后自动重启", rebindDelay.TotalSeconds);
                if (ReferenceEquals(_udp, udp)) _udp = null;
                udp.Dispose();
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
        // 与重绑路径互斥：停止后不能再把新绑定的套接字挂回 _udp，否则会泄漏。
        lock (_udpGate)
        {
            _udp?.Dispose();
            _udp = null;
        }
        _cts?.Dispose();
        _cts = null;
    }
}
