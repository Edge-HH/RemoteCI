using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server;

/// <summary>
/// 屏幕截图接口：<c>POST /api/classes/{classId}/screenshot</c> 让该班教室电脑截屏并等待上传完成，
/// 之后按编号取 PNG；插件凭命令中的一次性上传凭据向 <c>POST /api/plugin/screenshots/{id}</c> 上传。
/// 查看与发起都要求对该班拥有 CaptureScreen（系统管理员始终拥有）。
/// </summary>
public static class ScreenshotEndpoints
{
    public static void MapScreenshotEndpoints(this WebApplication app)
    {
        app.MapPost("/api/classes/{classId:guid}/screenshot", async (Guid classId, ScreenshotBody? body, HttpContext ctx,
            ScreenshotDispatcher dispatcher, IdentityCoordinator identities, ClassAccessService access, CancellationToken ct) =>
        {
            var principal = await ApiAuth.AuthorizeAsync(ctx, identities, ct);
            if (principal?.User is not { } user) return ApiAuth.Unauthorized();
            if (await PermissionsAsync(user, classId, access, ct) is not { } permissions ||
                !permissions.HasFlag(UserPermissions.CaptureScreen))
                return ApiAuth.Forbidden("没有该班级的屏幕截图权限");
            // 按设备截图只开放给系统管理员（设备状态页可看到同班的多台电脑）。
            if (body?.CredentialId is not null && user.Role != UserRole.Admin)
                return ApiAuth.Forbidden("仅系统管理员可以指定设备截图");
            var outcome = await dispatcher.CaptureAsync(classId, body?.CredentialId, user.WithPermissions(permissions), ct);
            return outcome.Record is { } record
                ? Results.Json(ScreenshotService.ToView(record), statusCode: StatusCodes.Status201Created)
                : Results.Json(outcome.Result, statusCode: Status(outcome.Result));
        });

        app.MapGet("/api/classes/{classId:guid}/screenshots", async (Guid classId, HttpContext ctx,
            ScreenshotService screenshots, IdentityCoordinator identities, ClassAccessService access, CancellationToken ct) =>
        {
            var principal = await ApiAuth.AuthorizeAsync(ctx, identities, ct);
            if (principal?.User is not { } user) return ApiAuth.Unauthorized();
            if (await PermissionsAsync(user, classId, access, ct) is not { } permissions ||
                !permissions.HasFlag(UserPermissions.CaptureScreen))
                return ApiAuth.Forbidden("没有该班级的屏幕截图权限");
            return Results.Ok(screenshots.ListForClass(classId).Select(ScreenshotService.ToView));
        });

        app.MapGet("/api/screenshots/{id}", async (string id, HttpContext ctx, ScreenshotService screenshots,
            IdentityCoordinator identities, ClassAccessService access, CancellationToken ct) =>
        {
            var principal = await ApiAuth.AuthorizeAsync(ctx, identities, ct);
            if (principal?.User is not { } user) return ApiAuth.Unauthorized();
            if (screenshots.Get(id) is not { } record ||
                await PermissionsAsync(user, record.ClassId, access, ct) is not { } permissions ||
                !permissions.HasFlag(UserPermissions.CaptureScreen))
                return NotFound();
            ctx.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(screenshots.ContentPath(record.Id), "image/png",
                $"screenshot-{record.CreatedAt.ToLocalTime():yyyyMMdd-HHmmss}.png");
        });

        // 插件上传：插件凭据 + 命令中下发的上传凭据，二者缺一不可；只接受一次。
        app.MapPost("/api/plugin/screenshots/{id}", async (string id, HttpContext ctx, ScreenshotService screenshots,
            IdentityCoordinator identities, CancellationToken ct) =>
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            var token = header.StartsWith($"{Protocol.BearerScheme} ", StringComparison.OrdinalIgnoreCase)
                ? header[(Protocol.BearerScheme.Length + 1)..].Trim()
                : string.Empty;
            var principal = token.Length == 0 ? null : await identities.ValidateAnyTokenAsync(token, ct);
            if (principal is null) return ApiAuth.Unauthorized();
            if (screenshots.GetPending(id) is not { } record ||
                !ScreenshotService.CanUpload(record, principal, ctx.Request.Headers[ScreenshotRequest.UploadKeyHeader].ToString()))
                return NotFound();
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = screenshots.MaxBytes + 1;
            if (ctx.Request.ContentLength > screenshots.MaxBytes)
                return ApiAuth.Error(ApiErrorCodes.InvalidRequest, "截图超过大小上限", StatusCodes.Status413PayloadTooLarge);
            try
            {
                await screenshots.StoreAsync(record, ctx.Request.Body, ct);
                return Results.NoContent();
            }
            catch (FileTransferException ex) { return ApiAuth.Error(ex.Code, ex.Message, ex.Status); }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return ApiAuth.Error(ApiErrorCodes.InvalidRequest, "截图超过大小上限", StatusCodes.Status413PayloadTooLarge);
            }
        });
    }

    /// <summary>用户在该班的有效权限；无权访问该班时返回 null。</summary>
    private static async Task<UserPermissions?> PermissionsAsync(
        UserProfile user, Guid classId, ClassAccessService access, CancellationToken ct)
    {
        if (user.Role == UserRole.Admin) return UserPermissions.All;
        var classes = await access.GetAccessibleClassesAsync(user.Id, user.Role, user.GrantedPermissions, ct);
        return classes.FirstOrDefault(x => x.Id == classId)?.Permissions;
    }

    internal static int Status(CommandResult result) => result.Code switch
    {
        CommandResultCodes.PluginOffline => StatusCodes.Status503ServiceUnavailable,
        CommandResultCodes.Timeout => StatusCodes.Status504GatewayTimeout,
        CommandResultCodes.Forbidden => StatusCodes.Status403Forbidden,
        CommandResultCodes.InvalidRequest => StatusCodes.Status400BadRequest,
        CommandResultCodes.CapabilityUnsupported => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    private static IResult NotFound() =>
        ApiAuth.Error(ApiErrorCodes.NotFound, "截图不存在或已过期", StatusCodes.Status404NotFound);
}

public sealed class ScreenshotBody
{
    /// <summary>仅系统管理员：指定同班的某台设备（插件凭据 Id）；缺省时截取该班最早接入的教室电脑。</summary>
    [JsonPropertyName("credentialId")] public Guid? CredentialId { get; set; }
}
