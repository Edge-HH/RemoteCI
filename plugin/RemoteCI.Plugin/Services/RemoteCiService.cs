using Microsoft.Extensions.Logging;
using Avalonia.Threading;
using RemoteCI.Plugin.Extensions;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 编排器：连接状态收集、局域网服务与云端客户端，负责消息路由。
/// 后续新增传输通道（如未来 watchOS 直连）只需在此注册，不改协议。
/// </summary>
public sealed class RemoteCiService : IDisposable
{
    private readonly StateCollector _collector;
    private readonly CommandHandler _commandHandler;
    private readonly ClassIslandNotificationBridge _notificationBridge;
    private readonly PluginSettings _settings;
    private readonly AccountMirror _accounts;
    private readonly CloudTokenStore _tokenStore;
    private readonly IRemoteCiExtensionRegistry _extensions;
    private readonly ScheduleSyncTaskCoordinator _scheduleSync;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<RemoteCiService> _logger;
    private LanServer? _lanServer;
    private CloudClient? _cloudClient;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _scheduleSyncTimeout;
    private ClassStateSnapshot? _latestSnapshot;
    private ScheduleBundle? _latestSchedule;

    public RemoteCiService(
        StateCollector collector,
        CommandHandler commandHandler,
        ClassIslandNotificationBridge notificationBridge,
        PluginSettings settings,
        AccountMirror accounts,
        CloudTokenStore tokenStore,
        IRemoteCiExtensionRegistry extensions,
        ScheduleSyncTaskCoordinator scheduleSync,
        ILoggerFactory loggerFactory)
    {
        _collector = collector;
        _commandHandler = commandHandler;
        _notificationBridge = notificationBridge;
        _settings = settings;
        _accounts = accounts;
        _tokenStore = tokenStore;
        _extensions = extensions;
        _scheduleSync = scheduleSync;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<RemoteCiService>();
    }

    public event Action<ScheduleSyncStatus>? ScheduleSyncStatusChanged;
    public event Action<CloudConnectionStatus>? CloudConnectionStatusChanged;

    public ScheduleSyncStatus? CurrentScheduleSyncStatus => _scheduleSync.Current;
    public CloudConnectionStatus CurrentCloudConnectionStatus =>
        _cloudClient?.CurrentStatus ?? CloudConnectionStatus.Stopped();

    public void Start()
    {
        if (_cts is not null)
        {
            // 重入保护：重复调用（如插件重载竞态）不能重复订阅事件与重复监听端口。
            _logger.LogWarning("RemoteCI 服务已在运行，忽略重复的启动调用");
            return;
        }
        _cts = new CancellationTokenSource();
        // ACL 收紧失败从此处记 Warning（静态防护类无法自行获取日志管道）。
        FileProtection.Logger = _loggerFactory.CreateLogger("RemoteCI.FileProtection");

        _collector.SnapshotPushed += OnSnapshotPushed;
        _collector.SchedulePushed += OnSchedulePushed;
        _collector.SchedulePushFailed += OnSchedulePushFailed;
        _collector.EventOccurred += OnEventOccurred;
        _commandHandler.NotificationSent += OnEventOccurred;
        _commandHandler.ScheduleChanged += OnScheduleChanged;
        _commandHandler.HostStateChanged += OnHostStateChanged;
        _notificationBridge.NotificationCaptured += OnEventOccurred;
        _extensions.ExtensionsChanged += OnExtensionsChanged;
        _scheduleSync.StatusChanged += OnScheduleSyncStatusChanged;
        _notificationBridge.Start();

        if (_settings.EnableLanServer)
        {
            _lanServer = new LanServer(
                _settings,
                _accounts,
                _commandHandler,
                RequestScheduleSync,
                () => _latestSnapshot,
                () => _latestSchedule,
                _loggerFactory.CreateLogger<LanServer>());
            _lanServer.Start();
        }

        // 即使云端被开发者开关禁用，也保留客户端状态对象，让普通设置页能明确显示“已关闭”。
        _cloudClient = new CloudClient(
            _settings,
            _accounts,
            _commandHandler,
            RequestScheduleSync,
            _loggerFactory.CreateLogger<CloudClient>(),
            tokenStore: _tokenStore);
        _cloudClient.Connected += OnCloudConnected;
        _cloudClient.ConnectionStatusChanged += OnCloudConnectionStatusChanged;
        _ = _cloudClient.StartAsync(_cts.Token);

        _collector.Start();
        PublishExtensions(); // 注册表可能在连接建立前已就绪，启动时先推送一次当前快照。
    }

    public void Stop()
    {
        _collector.Stop();
        _collector.SnapshotPushed -= OnSnapshotPushed;
        _collector.SchedulePushed -= OnSchedulePushed;
        _collector.SchedulePushFailed -= OnSchedulePushFailed;
        _collector.EventOccurred -= OnEventOccurred;
        _commandHandler.NotificationSent -= OnEventOccurred;
        _commandHandler.ScheduleChanged -= OnScheduleChanged;
        _commandHandler.HostStateChanged -= OnHostStateChanged;
        _notificationBridge.NotificationCaptured -= OnEventOccurred;
        _extensions.ExtensionsChanged -= OnExtensionsChanged;
        if (_scheduleSync.Current is { } active)
            _scheduleSync.TryFail(active.TaskId, "RemoteCI 服务已停止，课表任务已取消", out _);
        _scheduleSync.StatusChanged -= OnScheduleSyncStatusChanged;
        CancelScheduleSyncTimeout();
        _notificationBridge.Stop();
        _cts?.Cancel();
        _commandHandler.CancelPendingPowerActions();
        _commandHandler.StopVoiceMessage();
        if (_cloudClient is { } cloudClient)
        {
            // Dispose 会发布最终的“已停止”状态，先保留转发订阅供设置页刷新。
            cloudClient.Connected -= OnCloudConnected;
            cloudClient.Dispose();
            cloudClient.ConnectionStatusChanged -= OnCloudConnectionStatusChanged;
        }
        _cloudClient = null;
        _lanServer?.Dispose();
        _lanServer = null;
        // 置空释放重入标记，允许 Stop 后重新 Start；CTS 不紧跟 Dispose（令牌仍被
        // 后台任务引用），无原生资源交由 GC 回收。
        _cts = null;
    }

    /// <summary>由插件设置页触发同步；所有入口共享同一个任务闸门。</summary>
    public ScheduleSyncStatus PushCurrentSchedule()
    {
        if (_cts is null)
        {
            _logger.LogWarning("RemoteCI 服务尚未启动，无法手动推送课表");
            return new ScheduleSyncStatus
            {
                TaskId = Guid.NewGuid().ToString("N"),
                Source = ScheduleSyncSource.Plugin,
                State = ScheduleSyncTaskState.Failed,
                Message = "RemoteCI 服务尚未启动，暂时无法推送课表",
                FinishedAt = DateTimeOffset.UtcNow,
            };
        }

        return RequestScheduleSync(ScheduleSyncRequest.Create(ScheduleSyncSource.Plugin));
    }

    /// <summary>由插件设置页发起一次真实 WebSocket 通道测试，并在断线时立即跳过自动重连退避。</summary>
    public Task<CloudConnectionTestResult> TestCloudConnectionAsync(CancellationToken ct = default) =>
        _cloudClient is { } cloudClient
            ? cloudClient.TestConnectionAsync(ct)
            : Task.FromResult(new CloudConnectionTestResult(
                false,
                "测试失败：RemoteCI 服务尚未启动，请重启 ClassIsland。",
                CloudConnectionStatus.Stopped()));

    private void OnSnapshotPushed(ClassStateSnapshot snapshot)
    {
        _latestSnapshot = snapshot;
        _lanServer?.BroadcastState(snapshot);
        if (_cloudClient is { } cloud)
        {
            Observe(cloud.SendStateAsync(snapshot), "状态快照"); // 异步发送，不阻塞收集线程
        }
    }

    private void OnSchedulePushed(ScheduleBundle schedule) =>
        Observe(PublishScheduleAsync(schedule), "七日课表任务");

    private async Task PublishScheduleAsync(ScheduleBundle schedule)
    {
        _latestSchedule = schedule;
        var delivered = _lanServer?.BroadcastSchedule(schedule) == true;
        if (_cloudClient is { } cloud)
            delivered |= await cloud.SendScheduleAsync(schedule);

        if (_scheduleSync.Current is not { } active) return;
        if (delivered)
            _scheduleSync.TryComplete(active.TaskId, "课表已生成并推送完成", out _);
        else
            _scheduleSync.TryFail(active.TaskId, "课表已生成，但当前没有可用的服务端或手表连接", out _);
    }

    private void OnSchedulePushFailed(string error)
    {
        if (_scheduleSync.Current is { } active)
            _scheduleSync.TryFail(active.TaskId, $"生成课表失败：{error}", out _);
    }

    private void OnScheduleChanged() => Dispatcher.UIThread.Post(_collector.ForceSchedulePush);

    private ScheduleSyncStatus RequestScheduleSync(ScheduleSyncRequest request)
    {
        var status = _scheduleSync.TryStart(request);
        if (status.State != ScheduleSyncTaskState.Running) return status;

        ArmScheduleSyncTimeout(status.TaskId);
        // ClassIsland 课表服务只能在 UI 线程读取。
        Dispatcher.UIThread.Post(_collector.RequestSchedulePush);
        return status;
    }

    private void OnScheduleSyncStatusChanged(ScheduleSyncStatus status)
    {
        _logger.LogInformation(
            "课表任务状态：{TaskId} {Source} {State} - {Message}",
            status.TaskId, status.Source, status.State, status.Message);
        if (status.State is ScheduleSyncTaskState.Completed or ScheduleSyncTaskState.Failed)
            CancelScheduleSyncTimeout();

        _lanServer?.BroadcastScheduleSyncStatus(status);
        if (_cloudClient is { } cloud)
            Observe(cloud.SendScheduleSyncStatusAsync(status), "课表同步状态");
        ScheduleSyncStatusChanged?.Invoke(status);
    }

    private void ArmScheduleSyncTimeout(string taskId)
    {
        CancelScheduleSyncTimeout();
        var timeout = new CancellationTokenSource();
        var token = timeout.Token;
        _scheduleSyncTimeout = timeout;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), token);
                _scheduleSync.TryFail(taskId, "课表任务执行超时，请检查 ClassIsland 课表和网络连接", out _);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // 正常完成或服务停止。
            }
        });
    }

    private void CancelScheduleSyncTimeout()
    {
        var timeout = Interlocked.Exchange(ref _scheduleSyncTimeout, null);
        timeout?.Cancel();
        timeout?.Dispose();
    }

    private void OnHostStateChanged() => Dispatcher.UIThread.Post(_collector.ForceSnapshotPush);

    private void OnEventOccurred(ClassEvent @event)
    {
        _lanServer?.BroadcastEvent(@event);
        if (_cloudClient is { } cloud)
        {
            Observe(cloud.SendEventAsync(@event), "课程事件");
        }
    }

    private void OnExtensionsChanged(object? sender, EventArgs e) => PublishExtensions();

    // 首次连接或重连时补发当前扩展快照，避免注册早于 WebSocket 就绪时丢失 extensions_sync。
    private void OnCloudConnected(object? sender, EventArgs e) => PublishExtensions();

    private void OnCloudConnectionStatusChanged(CloudConnectionStatus status) =>
        CloudConnectionStatusChanged?.Invoke(status);

    private void PublishExtensions()
    {
        var definitions = _extensions.GetExtensions()
            .Select(ToDefinition)
            .ToList();
        _lanServer?.BroadcastExtensions(definitions);
        if (_cloudClient is { } cloud)
        {
            Observe(cloud.SendExtensionsAsync(definitions), "扩展清单");
        }
    }

    /// <summary>fire-and-forget 发送统一挂异常观察器，避免未观察异常在重连竞态下丢失。</summary>
    private void Observe(Task send, string what) =>
        _ = send.ContinueWith(
            task => _logger.LogDebug(task.Exception, "云端发送失败（{What}）", what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private static ExtensionDefinition ToDefinition(IRemoteCiExtension extension) => new()
    {
        Id = extension.Id,
        DisplayName = extension.DisplayName,
        Icon = extension.Icon,
        RequiredPermission = extension.RequiredPermission,
        Parameters = extension.Parameters.Count == 0 ? null : extension.Parameters.ToList(),
    };

    public void Dispose() => Stop();
}
