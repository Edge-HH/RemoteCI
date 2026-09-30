namespace RemoteCI.Server.Services;

public sealed class ServerOptions
{
    public const string SectionName = "Server";

    public string DatabasePath { get; set; } = "data/remoteci.db";
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
}
