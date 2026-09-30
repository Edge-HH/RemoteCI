using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Tests;

public sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdminUsername = "admin";
    public const string AdminPassword = "Test-Admin-Password-2026";
    public const string TestPairCode = "test-plugin-pair";
    private readonly SemaphoreSlim _pluginGate = new(1, 1);
    private string? _pluginToken;

    public TestWebApplicationFactory() : this(null, null, null) { }

    private TestWebApplicationFactory(
        string? databasePath,
        IReadOnlyDictionary<string, string?>? extraConfiguration,
        ILoggerProvider? loggerProvider)
    {
        DatabasePath = databasePath ?? Path.Combine(
            Path.GetTempPath(), "RemoteCI.Tests", Guid.NewGuid().ToString("N"), "remoteci.db");
        ExtraConfiguration = extraConfiguration;
        LoggerProvider = loggerProvider;
    }

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
                ["Server:BootstrapAdminPassword"] = AdminPassword,
                ["Server:BootstrapPluginPairCode"] = TestPairCode,
                ["Server:AccessTokenTtl"] = "01:00:00",
                ["Server:DeviceSessionTtl"] = "30.00:00:00",
                // 集成测试会高频调用登录端点，放开限流避免 429 干扰断言；锁定逻辑由专门测试覆盖。
                ["Server:AuthRateLimitPerMinute"] = "100000",
            };
            // 允许测试覆盖额外选项（如 LogBootstrapSecrets）。
            if (ExtraConfiguration is not null)
                foreach (var (key, value) in ExtraConfiguration)
                    values[key] = value;
            config.AddInMemoryCollection(values);
        });
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
