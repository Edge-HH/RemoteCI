using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class HolidayCalendarServiceTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.FromHours(8));

    internal sealed class FixedClock(DateTimeOffset now) : HolidayClock(TimeProvider.System)
    {
        public override DateTimeOffset Now => now;
    }

    internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests) Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request));
        }
    }

    internal static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    /// <summary>jsDelivr 故障、raw 可用、次年尚未发布：最常见的国内网络情形。</summary>
    internal static StubHandler TypicalSources() => new(request => request.RequestUri!.ToString() switch
    {
        var url when url.Contains("cdn.jsdelivr.net") => new HttpResponseMessage(HttpStatusCode.BadGateway),
        var url when url.EndsWith("/2026.json") => Json(HolidayTestData.Json2026),
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    });

    internal static HolidayHost Create(StubHandler handler) => new(handler);

    /// <summary>替换了时钟与 HTTP 处理器的测试宿主；登录也走同一个 TestServer。</summary>
    internal sealed class HolidayHost(StubHandler handler) : IAsyncDisposable
    {
        private readonly TestWebApplicationFactory _inner = new();
        private WebApplicationFactory<Program>? _app;

        public WebApplicationFactory<Program> App => _app ??= _inner.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<HolidayClock>(new FixedClock(Now));
                services.AddHttpClient(HolidayCalendarService.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            }));

        public HolidayCalendarService Service => App.Services.GetRequiredService<HolidayCalendarService>();

        public async Task<AuthResponse> LoginAsync(
            string username = TestWebApplicationFactory.AdminUsername,
            string password = TestWebApplicationFactory.AdminPassword)
        {
            var response = await App.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest
            {
                Username = username,
                Password = password,
                DeviceName = "Holiday Test",
            });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<AuthResponse>())!;
        }

        public async ValueTask DisposeAsync()
        {
            if (_app is not null) await _app.DisposeAsync();
            await _inner.DisposeAsync();
        }
    }

    [Fact]
    public async Task Refresh_FallsBackToRawAndIgnoresUnpublishedNextYear()
    {
        var handler = TypicalSources();
        await using var host = Create(handler);
        await host.App.CreateClient().GetAsync("/api/health");

        var status = await host.Service.RefreshAsync();

        Assert.Null(status.LastError);
        Assert.Equal(Now, status.LastSuccessAt);
        Assert.Contains(handler.Requests, x => x.StartsWith("https://raw.githubusercontent.com/") && x.EndsWith("/2026.json"));
        Assert.Contains(handler.Requests, x => x.EndsWith("/2027.json"));
        var calendar = await host.Service.BuildCalendarAsync();
        Assert.True(calendar.Enabled);
        Assert.Contains(calendar.Days, x => x is { Date: "2026-10-10", Kind: HolidayDayKinds.Makeup, FollowWeekday: 3 });
    }

    [Fact]
    public async Task Refresh_InvalidPayloadKeepsPreviousSnapshot()
    {
        var broken = false;
        var handler = new StubHandler(request => broken
            ? Json("{\"days\":[{\"name\":\"x\",\"date\":\"bad\",\"isOffDay\":true}]}")
            : request.RequestUri!.ToString().EndsWith("/2026.json") ? Json(HolidayTestData.Json2026) : new HttpResponseMessage(HttpStatusCode.NotFound));
        await using var host = Create(handler);
        await host.App.CreateClient().GetAsync("/api/health");
        await host.Service.RefreshAsync();

        broken = true;
        var status = await host.Service.RefreshAsync();

        Assert.NotNull(status.LastError);
        Assert.Equal(Now, status.LastSuccessAt);
        Assert.Contains((await host.Service.BuildCalendarAsync()).Days, x => x.Date == "2026-10-10");
    }

    [Fact]
    public async Task Refresh_UnreachableSourcesReportErrorWithoutSnapshot()
    {
        await using var host = Create(new StubHandler(_ => throw new HttpRequestException("network down")));
        await host.App.CreateClient().GetAsync("/api/health");

        var status = await host.Service.RefreshAsync();

        Assert.Contains("network down", status.LastError);
        Assert.Null(status.LastSuccessAt);
        Assert.Empty((await host.Service.BuildCalendarAsync()).Days);
    }

    [Fact]
    public async Task Refresh_UsesOnlyCustomTemplateWhenConfigured()
    {
        var handler = new StubHandler(request => request.RequestUri!.Host == "mirror.example.com" && request.RequestUri.AbsolutePath == "/h/2026.json"
            ? Json(HolidayTestData.Json2026)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        await using var host = Create(handler);
        await host.App.CreateClient().GetAsync("/api/health");

        var overview = await host.Service.UpdateSettingsAsync(true, "https://mirror.example.com/h/{year}.json");

        Assert.NotEmpty(handler.Requests);
        Assert.All(handler.Requests, x => Assert.StartsWith("https://mirror.example.com/", x));
        Assert.Null(overview.Status.LastError);
        Assert.Contains(overview.Periods, x => x.Name == "国庆节");
    }

    [Fact]
    public async Task Overview_GroupsUpcomingPeriodsAndRejectsNonMakeupOverride()
    {
        await using var host = Create(TypicalSources());
        await host.App.CreateClient().GetAsync("/api/health");
        await host.Service.RefreshAsync();
        await host.Service.SetOverrideAsync(new DateOnly(2026, 10, 10), null, null);
        await Assert.ThrowsAsync<HolidayOperationException>(() =>
            host.Service.SetOverrideAsync(new DateOnly(2026, 10, 9), 1, null));

        var overview = await host.Service.GetOverviewAsync();

        var national = Assert.Single(overview.Periods, x => x.Name == "国庆节");
        Assert.Equal(("2026-10-01", "2026-10-07"), (national.OffStart, national.OffEnd));
        Assert.Contains(national.MakeupDays, x => x is { Date: "2026-10-10", AutoWeekday: 3, FollowWeekday: null, FollowSource: HolidayFollowSources.Skip });
        Assert.DoesNotContain(overview.Periods, x => x.Name == "劳动节");
        Assert.Empty(overview.StaleOverrideDates);
    }

    [Fact]
    public async Task Disabled_BuildsEmptyCalendar()
    {
        await using var host = Create(TypicalSources());
        await host.App.CreateClient().GetAsync("/api/health");
        await host.Service.RefreshAsync();

        await host.Service.UpdateSettingsAsync(false, null);
        var calendar = await host.Service.BuildCalendarAsync();

        Assert.False(calendar.Enabled);
        Assert.Empty(calendar.Days);
    }
}
