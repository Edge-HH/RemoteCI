using System.Globalization;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server;

public sealed record HolidaySettingsBody(bool Enabled, string? SourceUrlTemplate);
public sealed record HolidayOverrideBody(int? FollowWeekday);

/// <summary>调休功能的 REST 接口；独立成文件，避免继续膨胀 Program.cs。</summary>
public static class HolidayEndpoints
{
    public static void MapHolidayEndpoints(this WebApplication app)
    {
        app.MapGet("/api/holidays", async (HttpContext ctx, IdentityCoordinator identities, HolidayCalendarService holidays, CancellationToken ct) =>
            await ApiAuth.AuthorizeAsync(ctx, identities, ct) is { User: not null }
                ? Results.Ok(await holidays.GetOverviewAsync(ct))
                : ApiAuth.Unauthorized());

        app.MapPut("/api/admin/holidays/settings", async (HolidaySettingsBody body, HttpContext ctx, IdentityCoordinator identities, HolidayCalendarService holidays, CancellationToken ct) =>
            await AdminAsync(ctx, identities, ct, async _ => Results.Ok(await holidays.UpdateSettingsAsync(body.Enabled, body.SourceUrlTemplate, ct))));

        app.MapPut("/api/admin/holidays/overrides/{date}", async (string date, HolidayOverrideBody body, HttpContext ctx, IdentityCoordinator identities, HolidayCalendarService holidays, CancellationToken ct) =>
            await AdminAsync(ctx, identities, ct, async principal =>
                Results.Ok(await holidays.SetOverrideAsync(ParseDate(date), body.FollowWeekday, principal.User!.Id, ct))));

        app.MapDelete("/api/admin/holidays/overrides/{date}", async (string date, HttpContext ctx, IdentityCoordinator identities, HolidayCalendarService holidays, CancellationToken ct) =>
            await AdminAsync(ctx, identities, ct, async _ => Results.Ok(await holidays.RemoveOverrideAsync(ParseDate(date), ct))));

        app.MapPost("/api/admin/holidays/refresh", async (HttpContext ctx, IdentityCoordinator identities, HolidayCalendarService holidays, CancellationToken ct) =>
            await AdminAsync(ctx, identities, ct, async _ => Results.Ok(await holidays.RefreshAsync(ct))));
    }

    private static DateOnly ParseDate(string value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new HolidayOperationException("日期格式应为 yyyy-MM-dd");

    private static async Task<IResult> AdminAsync(
        HttpContext ctx, IdentityCoordinator identities, CancellationToken ct, Func<AuthPrincipal, Task<IResult>> action)
    {
        var principal = await ApiAuth.AuthorizeAsync(ctx, identities, ct);
        if (principal?.User is null) return ApiAuth.Unauthorized();
        if (principal.User.Role != UserRole.Admin) return ApiAuth.Forbidden();
        try { return await action(principal); }
        catch (Exception ex) when (ex is HolidayOperationException or ArgumentException)
        {
            return Results.BadRequest(new ApiError { Code = ApiErrorCodes.InvalidRequest, Message = ex.Message });
        }
    }
}
