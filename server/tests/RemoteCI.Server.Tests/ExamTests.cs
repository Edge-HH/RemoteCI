using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Server.Tests;

public sealed class ExamTests
{
    [Fact]
    public async Task ExamPage_ListsTheClassUnderManagement()
    {
        await using var factory = new TestWebApplicationFactory();
        var auth = await factory.LoginAsync();
        using var browser = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var ticket = await factory.CreateClient().SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/auth/web-ticket", auth.AccessToken));
        var path = (await ticket.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString()!;
        (await browser.GetAsync(path)).EnsureSuccessStatusCode();

        var html = WebUtility.HtmlDecode(await browser.GetStringAsync("/Exams"));
        Assert.Contains("安装到所选班级", html);
        Assert.Contains(TestWebApplicationFactory.DefaultClassName, html);
        Assert.Contains(">管理 · 考试</a>", html);
    }

    [Fact]
    public async Task Bind_AcceptsPersistentPairCode_AndDoesNotCreatePluginCredential()
    {
        await using var factory = new TestWebApplicationFactory();
        string code;
        int credentialsBefore;
        using (var scope = factory.Services.CreateScope())
        {
            var identities = scope.ServiceProvider.GetRequiredService<IdentityCoordinator>();
            code = await identities.SetClassPairingCodeAsync(TestWebApplicationFactory.DefaultClassId, "exam-pair-code");
            credentialsBefore = scope.ServiceProvider.GetRequiredService<RemoteCI.Server.Data.AppDbContext>()
                .PluginCredentials.Count();
        }

        using var client = factory.CreateClient();
        var denied = await client.PostAsJsonAsync("/api/exam/bind", new ExamBindRequest { PairCode = "not-a-real-code" });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var response = await client.PostAsJsonAsync("/api/exam/bind", new ExamBindRequest
        {
            PairCode = code,
            DeviceName = "ExamAware",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bound = (await response.Content.ReadFromJsonAsync<ExamBindResponse>())!;
        Assert.Equal(TestWebApplicationFactory.DefaultClassId, bound.ClassId);
        Assert.Equal(TestWebApplicationFactory.DefaultClassName, bound.ClassName);
        Assert.False(string.IsNullOrWhiteSpace(bound.Token));

        using (var scope = factory.Services.CreateScope())
        {
            var count = scope.ServiceProvider.GetRequiredService<RemoteCI.Server.Data.AppDbContext>().PluginCredentials.Count();
            Assert.Equal(credentialsBefore, count);
        }

        var jobs = await client.SendAsync(TestWebApplicationFactory.Bearer(HttpMethod.Get, "/api/exam/jobs", bound.Token));
        Assert.Equal(HttpStatusCode.OK, jobs.StatusCode);
        Assert.Contains("\"jobs\":[]", await jobs.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ticket_FromAssignedPlugin_BindsTheSameClass()
    {
        await using var factory = new TestWebApplicationFactory();
        var plugin = await factory.GetPluginTokenAsync();
        using var client = factory.CreateClient();
        var ticketResponse = await client.SendAsync(
            TestWebApplicationFactory.Bearer(HttpMethod.Post, "/api/exam/tickets", plugin));
        Assert.Equal(HttpStatusCode.OK, ticketResponse.StatusCode);
        var ticket = (await ticketResponse.Content.ReadFromJsonAsync<ExamTicketResponse>())!;
        Assert.Equal(TestWebApplicationFactory.DefaultClassId, ticket.ClassId);

        var boundResponse = await client.PostAsJsonAsync("/api/exam/bind", new ExamBindRequest
        {
            Ticket = ticket.Ticket,
            DeviceName = "ExamAware",
        });
        var bound = (await boundResponse.Content.ReadFromJsonAsync<ExamBindResponse>())!;
        Assert.Equal(ticket.ClassId, bound.ClassId);

        var ack = await client.SendAsync(TestWebApplicationFactory.Bearer(
            HttpMethod.Post, $"/api/exam/jobs/{Guid.NewGuid()}/ack", bound.Token, new ExamJobAck { State = "started" }));
        Assert.Equal(HttpStatusCode.BadRequest, ack.StatusCode);
    }
}
