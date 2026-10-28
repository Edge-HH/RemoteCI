namespace RemoteCI.Server.Services;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string DatabasePath { get; set; } = "data/remoteci.db";
    public string WebPushSubject { get; set; } = "https://github.com/Edge-HH/RemoteCI";
    public string BootstrapAdminUsername { get; set; } = "admin";
    public string? BootstrapAdminPassword { get; set; }
    public string? BootstrapPluginPairCode { get; set; }
    public TimeSpan AccessTokenTtl { get; set; } = TimeSpan.FromHours(1);
    public TimeSpan DeviceSessionTtl { get; set; } = TimeSpan.FromDays(30);

    /// <summary>在线 WebSocket 连接重新查询持久化授权状态的兜底周期。</summary>
    public TimeSpan ConnectionAuthorizationRefreshInterval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>认证端点（登录/刷新/插件配对）每个来源 IP 每分钟的请求上限。</summary>
    public int AuthRateLimitPerMinute { get; set; } = 20;

    /// <summary>
    /// 首次启动自动生成管理员密码/插件配对码时是否写入日志。
    /// 默认开启（容器无环境变量部署时这是唯一获取途径）；日志会被集中收集的生产环境建议关闭，
    /// 改用 REMOTECI_ADMIN_PASSWORD / REMOTECI_PLUGIN_PAIR_CODE 环境变量。
    /// </summary>
    public bool LogBootstrapSecrets { get; set; } = true;

    /// <summary>是否在后台定时拉取节假日数据；测试环境关闭以免访问外网。</summary>
    public bool HolidayAutoRefresh { get; set; } = true;

    /// <summary>
    /// 是否运行集控运维的定时后台任务（在线记录、离线告警、自动化、课时台账、每日档案差异检查）。
    /// 生产环境保持开启；统计数据库命令数的性能回归测试关闭，以免后台查询干扰计数。
    /// </summary>
    public bool OperationsWorkers { get; set; } = true;

    /// <summary>文件中转单个文件上限（字节），默认 2 GiB；不能超过协议上限 4 GiB。反向代理也需放开请求体大小。</summary>
    public long FileTransferMaxBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>中转文件在服务端的保留时长，过期后删除；设备在此期间可断点续传。</summary>
    public TimeSpan FileTransferRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>中转目录总容量上限（字节），默认 20 GiB；0 表示不限制。</summary>
    public long FileTransferQuotaBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    /// <summary>ClassIsland 插件市场索引地址（index.zip，{time} 会替换为时间戳）；留空使用官方地址。</summary>
    public string? PluginMarketIndexUrl { get; set; }

    /// <summary>
    /// 服务端预下载插件包时优先使用的市场镜像键（例如 github、ghproxy、moeyy，以索引中的 DownloadMirrors 为准）；
    /// 留空时先用 github，失败后依次回退其余镜像。
    /// </summary>
    public string? PluginMarketMirror { get; set; }

    /// <summary>单张屏幕截图上限（字节），默认 20 MiB，也是协议上限。</summary>
    public int ScreenshotMaxBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>屏幕截图在服务端的保留时长，过期后删除；每个班级另外最多保留 30 张。</summary>
    public TimeSpan ScreenshotRetention { get; set; } = TimeSpan.FromHours(24);
}
