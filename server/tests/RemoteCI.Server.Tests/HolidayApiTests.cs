using System.Net;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class HolidayApiTests
{
    [Fact]
    public async Task ReadRequiresLoginAndWritesRequireAdmin()
    {
        await using var host = HolidayCalendarServiceTests.Create(HolidayCalendarServiceTests.TypicalSources());
        var client = host.App.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/holidays")).StatusCode);

        var admin = await host.LoginAsync();
        var created = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", admin.AccessToken,
            new CreateUserRequest { Username = "holiday.reader", DisplayName = "普通用户", Password = "Holiday-Reader-2026" }));
        created.EnsureSuccessStatusCode();
        var reader = await host.LoginAsync("holiday.reader", "Holiday-Reader-2026");

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/holidays", reader.AccessToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put, "/api/admin/holidays/overrides/2026-10-10", reader.AccessToken, new { followWeekday = 5 }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, "/api/admin/holidays/refresh", reader.AccessToken))).StatusCode);
    }

    [Fact]
    public async Task OverrideRoundTripAndValidation()
    {
        await using var host = HolidayCalendarServiceTests.Create(HolidayCalendarServiceTests.TypicalSources());
        var client = host.App.CreateClient();
        var admin = await host.LoginAsync();
        (await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/admin/holidays/refresh", admin.AccessToken))).EnsureSuccessStatusCode();

        var set = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put, "/api/admin/holidays/overrides/2026-10-10", admin.AccessToken, new { followWeekday = 5 }));
        set.EnsureSuccessStatusCode();
        Assert.Contains("\"followSource\":\"manual\"", await set.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put, "/api/admin/holidays/overrides/2026-10-09", admin.AccessToken, new { followWeekday = 1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put, "/api/admin/holidays/overrides/10-10", admin.AccessToken, new { followWeekday = 1 }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Put, "/api/admin/holidays/overrides/2026-10-10", admin.AccessToken, new { followWeekday = 6 }))).StatusCode);

        var cleared = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Delete, "/api/admin/holidays/overrides/2026-10-10", admin.AccessToken));
        cleared.EnsureSuccessStatusCode();
        Assert.Contains("\"date\":\"2026-10-10\",\"autoWeekday\":3,\"followWeekday\":3,\"followSource\":\"auto\"",
            await cleared.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Settings_RejectInvalidTemplate()
    {
        await using var host = HolidayCalendarServiceTests.Create(HolidayCalendarServiceTests.TypicalSources());
        var client = host.App.CreateClient();
        var admin = await host.LoginAsync();

        var response = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Put, "/api/admin/holidays/settings",
            admin.AccessToken, new { enabled = true, sourceUrlTemplate = "http://insecure/{year}.json" }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
