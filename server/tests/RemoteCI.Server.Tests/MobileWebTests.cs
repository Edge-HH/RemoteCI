using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class MobileWebTests
{
    private const string Phone = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148";
    private static string Csrf(string html) => WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
    private static async Task<HttpClient> BrowserAsync(TestWebApplicationFactory factory, AuthResponse auth, bool phone = true)
    {
        var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var ticket = await factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/auth/web-ticket", auth.AccessToken));
        var path = (await ticket.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString()!;
        (await browser.GetAsync(path)).EnsureSuccessStatusCode();
        if (phone) browser.DefaultRequestHeaders.UserAgent.ParseAdd(Phone);
        return browser;
    }

    [Fact]
    public async Task PhoneSelection_DesktopOverride_IsPersistent_AndDoesNotRedirectHandlersOrApi()
    {
        await using var factory = new TestWebApplicationFactory();
        using var browser = await BrowserAsync(factory, await factory.LoginAsync());
        Assert.Equal("/Mobile/Today", (await browser.GetAsync("/")).Headers.Location?.OriginalString);
        Assert.Equal("/Mobile/Schedule", (await browser.GetAsync("/Schedule")).Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Mobile/Today?handler=State")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/state")).StatusCode);
        Assert.Equal("/Index", (await browser.GetAsync("/Mobile/Today?view=desktop")).Headers.Location?.OriginalString);
        var desktop = await browser.GetStringAsync("/Index");
        Assert.Contains("width=device-width, initial-scale=1", desktop); Assert.DoesNotContain("width=1280", desktop); Assert.DoesNotContain("data-mobile-ui", desktop);
        Assert.Equal("/Mobile/Today", (await browser.GetAsync("/Index?view=mobile")).Headers.Location?.OriginalString);
        Assert.Contains("page=%2FProfiles", (await browser.GetAsync("/Profiles")).Headers.Location?.OriginalString);
        var post = await browser.PostAsync("/Schedule", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, post.StatusCode);
    }

    [Fact]
    public async Task StudentWithoutDesktopPermission_CanUseMobile_AndCannotManageOthers()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        (await factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", owner.AccessToken,
            new CreateUserRequest { Username = "mobile-student", DisplayName = "手机学生", Password = "Test-Student-Password", RoleId = AccountRole.StudentId, ClassId = TestWebApplicationFactory.DefaultClassId }))).EnsureSuccessStatusCode();
        using var browser = await BrowserAsync(factory, await factory.LoginAsync("mobile-student", "Test-Student-Password"));
        foreach (var path in new[] { "/Mobile/Today", "/Mobile/Schedule", "/Mobile/MySchedule", "/Mobile/Account", "/Mobile/Inbox", "/Mobile/Calendar", "/Mobile/Control" })
        {
            var response = await browser.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, path + ": " + response.StatusCode + " " + response.Headers.Location);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("更多操作请进入", html); Assert.Contains("data-mobile-ui", html);
        }
        Assert.Equal("/Denied", (await browser.GetAsync("/Mobile/Manage")).Headers.Location?.OriginalString);
        Assert.Equal("/Denied", (await browser.GetAsync("/Mobile/Users")).Headers.Location?.OriginalString);
        var desktop = await browser.GetAsync("/Index?view=desktop");
        Assert.Equal("/Index", desktop.Headers.Location?.OriginalString);
        Assert.Equal("/Denied", (await browser.GetAsync("/Index")).Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task MobilePageModels_RenderSharedForms_AndPersonalCourseWritesWork()
    {
        await using var factory = new TestWebApplicationFactory();
        using var browser = await BrowserAsync(factory, await factory.LoginAsync());
        foreach (var path in new[] { "Today", "Schedule", "MySchedule", "Control", "Users", "Account", "Inbox", "SystemConfig", "Manage", "Pairing", "VisitorAccess", "Calendar" })
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Mobile/" + path)).StatusCode);
        var html = await browser.GetStringAsync("/Mobile/MySchedule");
        var start = DateTimeOffset.UtcNow.AddDays(2);
        var response = await browser.PostAsync("/Mobile/MySchedule?handler=Save", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = Csrf(html), ["Subject"] = "移动端课程", ["StartsAt"] = start.ToString("O"), ["EndsAt"] = start.AddHours(1).ToString("O"), ["NotifyAtStart"] = "true" }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("移动端课程", WebUtility.HtmlDecode(await browser.GetStringAsync("/Mobile/MySchedule")));
        var noCsrf = await browser.PostAsJsonAsync("/Mobile/Inbox?handler=Subscribe", new WebPushSubscriptionRequest());
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
    }

    [Fact]
    public async Task CalendarStableUid_Versions_Cancellations_AndRevocation_AreIndependentOfBrowserCookies()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        string token; Guid courseId; Guid subscriptionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == owner.User.Id);
            var courses = scope.ServiceProvider.GetRequiredService<PersonalScheduleService>();
            var start = DateTimeOffset.UtcNow.AddDays(3).ToOffset(TimeSpan.FromHours(8));
            courseId = (await courses.SaveAsync(user.Id, null, new PersonalCourseRequest { Subject = "中文,课程;\\测试\n第二行", Location = "会议室", StartsAt = start, EndsAt = start.AddHours(1), ReminderMinutes = 10, NotifyAtStart = true })).Id;
            var created = await scope.ServiceProvider.GetRequiredService<CalendarFeedService>().CreateAsync(user, null, 10, 480, default);
            token = created.Token; subscriptionId = created.Subscription.Id;
        }
        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/calendar/{token}.ics");
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        var first = await response.Content.ReadAsStringAsync();
        var uid = Regex.Match(first, @"UID:([^\r]+)").Groups[1].Value;
        Assert.Contains("\\,课程\\;\\\\测试\\n第二行", first);
        Assert.Equal(2, Regex.Matches(first, "BEGIN:VALARM").Count);
        Assert.Equal(first, await client.GetStringAsync($"/calendar/{token}.ics"));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var course = await db.PersonalCourses.SingleAsync(x => x.Id == courseId);
            course.Subject = "改名后课程"; course.StartsAtUnixMs += 60_000; course.EndsAtUnixMs += 60_000;
            await db.SaveChangesAsync();
        }
        var changed = await client.GetStringAsync($"/calendar/{token}.ics");
        Assert.Contains("UID:" + uid, changed); Assert.Contains("SEQUENCE:2", changed);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().PersonalCourses.Where(x => x.Id == courseId).ExecuteDeleteAsync();
        Assert.Contains("STATUS:CANCELLED", await client.GetStringAsync($"/calendar/{token}.ics"));
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().CalendarSubscriptions.Where(x => x.Id == subscriptionId).ExecuteUpdateAsync(x => x.SetProperty(y => y.Revoked, true));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/calendar/{token}.ics")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/calendar/not-a-token.ics")).StatusCode);
    }

    [Fact]
    public async Task ClassCalendar_RechecksMembership_AndRejectsInaccessibleClasses()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        var create = await factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/users", owner.AccessToken,
            new CreateUserRequest { Username = "calendar-student", DisplayName = "日历学生", Password = "Test-Calendar-Password", RoleId = AccountRole.StudentId, ClassId = TestWebApplicationFactory.DefaultClassId }));
        var id = (await create.Content.ReadFromJsonAsync<UserListItem>())!.Id;
        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(x => x.Id == id);
            var feed = scope.ServiceProvider.GetRequiredService<CalendarFeedService>();
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => feed.CreateAsync(user, Guid.NewGuid(), 10, 480, default));
            token = (await feed.CreateAsync(user, TestWebApplicationFactory.DefaultClassId, null, 480, default)).Token;
        }
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().GetAsync($"/calendar/{token}.ics")).StatusCode);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().ClassMemberships.Where(x => x.UserId == id).ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await factory.CreateClient().GetAsync($"/calendar/{token}.ics")).StatusCode);
    }

    [Fact]
    public async Task BrowserSubscriptionsFollowClass_AndLogoutDetachesOnlyTheCurrentBrowser()
    {
        await using var factory = new TestWebApplicationFactory();
        var owner = await factory.LoginAsync();
        using var first = await BrowserAsync(factory, owner);
        using var second = await BrowserAsync(factory, owner);
        var keys = WebPushSender.GenerateKeys();
        var firstToken = "";
        foreach (var (browser, name) in new[] { (first, "first"), (second, "second") })
        {
            var token = Csrf(await browser.GetStringAsync("/Mobile/Inbox"));
            if (name == "first") firstToken = token;
            using var request = new HttpRequestMessage(HttpMethod.Post, "/Mobile/Inbox?handler=Subscribe")
            { Content = JsonContent.Create(new WebPushSubscriptionRequest { Endpoint = "https://web.push.apple.com/" + name,
                P256dh = keys.PublicKey, Auth = WebPushSender.Base64Url(new byte[16]), EventMask = 1 }) };
            request.Headers.Add("X-CSRF-TOKEN", token);
            (await browser.SendAsync(request)).EnsureSuccessStatusCode();
        }
        var newClassResponse = await factory.CreateClient().SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/classes", owner.AccessToken,
            new CreateClassRequest { Name = "通知切换班级" }));
        var newClass = (await newClassResponse.Content.ReadFromJsonAsync<ClassDetail>())!;
        // A subscription created before the first class was assigned must start following class selection too.
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().WebPushSubscriptions.Where(x => x.Endpoint.EndsWith("/first"))
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.ClassId, (Guid?)null));
        var switched = await first.PostAsync("/Account?handler=SwitchClass", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["__RequestVerificationToken"] = firstToken, ["classId"] = newClass.Id.ToString(), ["returnUrl"] = "/Mobile/Today" }));
        Assert.Equal(HttpStatusCode.Redirect, switched.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(newClass.Id, (await db.WebPushSubscriptions.SingleAsync(x => x.Endpoint.EndsWith("/first"))).ClassId);
            Assert.Equal(TestWebApplicationFactory.DefaultClassId, (await db.WebPushSubscriptions.SingleAsync(x => x.Endpoint.EndsWith("/second"))).ClassId);
            var queue = scope.ServiceProvider.GetRequiredService<WebPushQueue>();
            var oldEvent = new ClassEvent { Id = "same-event", Event = ClassEventKind.OnClass, OccurredAt = DateTimeOffset.UtcNow.AddMinutes(-10) };
            await queue.ClassEventAsync(newClass.Id, oldEvent, default);
            await queue.ClassEventAsync(newClass.Id, oldEvent, default);
            Assert.Single(await db.WebPushJobs.ToListAsync());
        }
        Assert.Equal(HttpStatusCode.Redirect, (await first.PostAsync("/Logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = firstToken }))).StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var subs = await scope.ServiceProvider.GetRequiredService<AppDbContext>().WebPushSubscriptions.ToListAsync();
            Assert.EndsWith("/second", Assert.Single(subs).Endpoint);
        }
    }

    [Fact]
    public async Task DurableQueue_RetriesHonorsExpiry_AndDeletesGoneSubscriptions()
    {
        await using var parent = new TestWebApplicationFactory();
        var transport = new FakeTransport();
        await using var factory = parent.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            var worker = services.FirstOrDefault(x => x.ServiceType == typeof(IHostedService) && x.ImplementationType == typeof(WebPushWorker));
            if (worker is not null) services.Remove(worker);
            services.AddSingleton<IHttpClientFactory>(transport);
        }));
        _ = factory.CreateClient();
        Guid subId; Guid jobId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.FirstAsync();
            var key = WebPushSender.GenerateKeys();
            var sub = new WebPushSubscription { Id = Guid.NewGuid(), UserId = user.Id, Endpoint = "https://web.push.apple.com/test", P256dh = key.PublicKey, Auth = WebPushSender.Base64Url(new byte[16]), CreatedAt = DateTimeOffset.UtcNow };
            db.WebPushSubscriptions.Add(sub); await db.SaveChangesAsync(); subId = sub.Id;
            scope.ServiceProvider.GetRequiredService<WebPushQueue>().Add(sub, "test-queue", new { title = "test", body = "test" }, DateTimeOffset.UtcNow.AddHours(1));
            scope.ServiceProvider.GetRequiredService<WebPushQueue>().Add(sub, "expired-test", new { title = "expired" }, DateTimeOffset.UtcNow.AddMinutes(-1));
            await db.SaveChangesAsync(); jobId = (await db.WebPushJobs.SingleAsync(x => x.SourceId == "test-queue")).Id;
        }
        var workerInstance = new WebPushWorker(factory.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System,
            NullLogger<WebPushWorker>.Instance, factory.Services.GetRequiredService<WebPushSignal>());
        await workerInstance.DispatchAsync(default);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var job = await db.WebPushJobs.SingleAsync(x => x.Id == jobId);
            Assert.True((await db.WebPushJobs.SingleAsync(x => x.SourceId == "expired-test")).Finished);
            Assert.Equal(1, transport.Sends);
            Assert.False(job.Finished); Assert.Equal(1, job.Attempts); Assert.True(job.DueAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            job.DueAt = 0; await db.SaveChangesAsync();
        }
        transport.Status = HttpStatusCode.Gone;
        await workerInstance.DispatchAsync(default);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.False(await db.WebPushSubscriptions.AnyAsync(x => x.Id == subId));
            Assert.False(await db.WebPushJobs.AnyAsync(x => x.Id == jobId));
        }
        Assert.Equal(2, transport.Sends);
    }

    [Theory]
    [InlineData("https://web.push.apple.com/test", true)]
    [InlineData("https://fcm.googleapis.com/test", true)]
    [InlineData("https://localhost/test", false)]
    [InlineData("https://push.apple.com.attacker.example/test", false)]
    [InlineData("https://push.apple.com:8443/test", false)]
    public void PushEndpoints_AreRestrictedToBrowserPushServices(string value, bool allowed) => Assert.Equal(allowed, WebPushQueue.IsTrustedEndpoint(value));

    [Fact]
    public void CalendarFolding_RespectsUtf8OctetsWithoutSplittingCharacters()
    {
        var folded = CalendarFeedService.Fold("SUMMARY:" + new string('课', 100));
        Assert.All(folded.Split("\r\n"), line => Assert.True(System.Text.Encoding.UTF8.GetByteCount(line) <= 75));
        Assert.Equal("SUMMARY:" + new string('课', 100), folded.Replace("\r\n ", ""));
    }

    private sealed class FakeTransport : HttpMessageHandler, IHttpClientFactory
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.ServiceUnavailable;
        public int Sends;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Sends); return Task.FromResult(new HttpResponseMessage(Status)); }
    }
}
