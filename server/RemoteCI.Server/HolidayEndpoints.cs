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
            await AuthorizeAsync(ctx, identities, ct) is { User: not null }
                ? Results.Ok(await holidays.GetOverviewAsync(ct))
                : Unauthorized());

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
        var principal = await AuthorizeAsync(ctx, identities, ct);
        if (principal?.User is null) return Unauthorized();
        if (principal.User.Role != UserRole.Admin) return Results.Json(
            new ApiError { Code = ApiErrorCodes.Forbidden, Message = "权限不足" }, statusCode: StatusCodes.Status403Forbidden);
        try { return await action(principal); }
        catch (Exception ex) when (ex is HolidayOperationException or ArgumentException)
        {
            return Results.BadRequest(new ApiError { Code = ApiErrorCodes.InvalidRequest, Message = ex.Message });
        }
    }

    // 与 Program.cs 中的同名本地函数一致；顶级语句里的本地函数无法跨文件复用。
    private static async Task<AuthPrincipal?> AuthorizeAsync(HttpContext ctx, IdentityCoordinator identities, CancellationToken ct)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        var token = header.StartsWith($"{Protocol.BearerScheme} ", StringComparison.OrdinalIgnoreCase)
            ? header[(Protocol.BearerScheme.Length + 1)..].Trim()
            : ctx.Request.Headers["X-API-Key"].ToString().Trim();
        if (string.IsNullOrWhiteSpace(token)) return null;
        return token.StartsWith(IdentityCoordinator.ApiKeyPrefix, StringComparison.Ordinal)
            ? await identities.ValidateApiKeyAsync(token, ct)
            : await identities.ValidateAccessTokenAsync(token, ct);
    }

    private static IResult Unauthorized() => Results.Json(
        new ApiError { Code = ApiErrorCodes.Unauthorized, Message = "未登录或登录已失效" },
        statusCode: StatusCodes.Status401Unauthorized);
}
