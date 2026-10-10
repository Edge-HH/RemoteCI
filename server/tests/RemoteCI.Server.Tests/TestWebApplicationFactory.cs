using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteCI.Server.Data;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Tests;

public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdminUsername = "admin";
    public const string AdminPassword = "Test-Admin-Password-2026";
    public const string TestPairCode = "test-plugin-pair";

    /// <summary>
    /// 测试夹具预先建好的班级：新部署不再自动创建默认班级，夹具模拟“管理员已新建第一个班级”，
    /// 并把引导配对码绑定到它，插件配对后直接归属该班。Id 与旧版默认班级不同，避免与升级测试混淆。
    /// </summary>
    public static readonly Guid DefaultClassId = Guid.Parse("7e57c1a5-0000-4000-8000-000000000001");
    public const string DefaultClassName = "测试班级";
    private readonly SemaphoreSlim _pluginGate = new(1, 1);
    private string? _pluginToken;

    public TestWebApplicationFactory() : this(null, null, null) { }

    private TestWebApplicationFactory(
        string? databasePath,
        IReadOnlyDictionary<string, string?>? extraConfiguration,
        ILoggerProvider? loggerProvider,
        bool freshInstall = false)
    {
        DatabasePath = databasePath ?? Path.Combine(
            Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "remoteci.db");
        ExtraConfiguration = extraConfiguration;
        LoggerProvider = loggerProvider;
        FreshInstall = freshInstall;
    }

    /// <summary>全新部署：不预置管理员密码与班级，用于测试初始化向导。</summary>
    public bool FreshInstall { get; }

    public static TestWebApplicationFactory ForFreshInstall() => new(null, null, null, freshInstall: true);

    public string DatabasePath { get; }
    private IReadOnlyDictionary<string, string?>? ExtraConfiguration { get; }
    private ILoggerProvider? LoggerProvider { get; }

    public static TestWebApplicationFactory ForDatabase(
        string databasePath,
        IReadOnlyDictionary<string, string?>? extraConfiguration = null) =>
        new(databasePath, extraConfiguration, null);

    public static TestWebApplicationFactory ForDatabaseAndLogger(
        string databasePath,
        ILoggerProvider loggerProvider,
        IReadOnlyDictionary<string, string?>? extraConfiguration = null) =>
        new(databasePath, extraConfiguration, loggerProvider);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        if (LoggerProvider is not null)
            builder.ConfigureLogging(logging => logging.AddProvider(LoggerProvider));
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["Server:DatabasePath"] = DatabasePath,
                ["Server:BootstrapAdminUsername"] = AdminUsername,
                ["Server:BootstrapAdminPassword"] = FreshInstall ? null : AdminPassword,
                ["Server:BootstrapPluginPairCode"] = TestPairCode,
                ["Server:AccessTokenTtl"] = "01:00:00",
                ["Server:DeviceSessionTtl"] = "30.00:00:00",
                // 集成测试会高频调用登录端点，放开限流避免 429 干扰断言；锁定逻辑由专门测试覆盖。
                ["Server:AuthRateLimitPerMinute"] = "100000",
                // 后台节假日刷新会访问外网；测试按需直接调用 HolidayCalendarService。
                ["Server:HolidayAutoRefresh"] = "false",
            };
            // 允许测试覆盖额外选项（如 LogBootstrapSecrets）。
            if (ExtraConfiguration is not null)
                foreach (var (key, value) in ExtraConfiguration)
                    values[key] = value;
            config.AddInMemoryCollection(values);
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        if (FreshInstall) return host;
        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!db.Classrooms.Any(x => x.Id == DefaultClassId))
        {
            var now = DateTimeOffset.UtcNow;
            db.Classrooms.Add(new Classroom { Id = DefaultClassId, Name = DefaultClassName, CreatedAt = now.AddYears(-1), UpdatedAt = now });
            db.SaveChanges();
        }
        db.PluginPairingCodes.Where(x => x.ClassroomId == null && !x.IsShared)
            .ExecuteUpdate(setters => setters.SetProperty(x => x.ClassroomId, (Guid?)DefaultClassId));
        return host;
    }

    public async Task<AuthResponse> LoginAsync(string username = AdminUsername, string password = AdminPassword)
    {
        var response = await CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Username = username,
            Password = password,
            DeviceName = "Integration Test",
        });
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Login failed ({response.StatusCode}): {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
    }

    public async Task<string> GetPluginTokenAsync()
    {
        if (_pluginToken is not null) return _pluginToken;
        await _pluginGate.WaitAsync();
        try
        {
            if (_pluginToken is not null) return _pluginToken;
            var response = await CreateClient().PostAsJsonAsync("/api/plugin/pair", new PairRequest
            {
                PairCode = TestPairCode,
                Role = "plugin",
            });
            response.EnsureSuccessStatusCode();
            _pluginToken = (await response.Content.ReadFromJsonAsync<PairResponse>())!.Token;
            return _pluginToken;
        }
        finally { _pluginGate.Release(); }
    }

    public static HttpRequestMessage Bearer(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
