using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server;

/// <summary>
/// ClassIsland 设置接口：供 AstrBot 等外部客户端读取与修改教室端 ClassIsland 设置。
/// 命令 37/38 是 serverOnly 命令（通用命令通道一律拒绝），这里的两个端点是它们唯一的 API 入口，
/// 与档案收集/应用、屏幕截图的做法一致：服务端校验目录与权限后定向发送并等待回执。
/// </summary>
public static class ClassIslandSettingsEndpoints
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    public static void MapClassIslandSettingsEndpoints(this WebApplication app)
    {
        // 设置项目录：键、类型、范围与界面文案，供客户端构建表单或解析用户输入；任何已认证账号可读。
        app.MapGet("/api/classisland-settings/catalog", async (HttpContext ctx, IdentityCoordinator identities, CancellationToken ct) =>
        {
            var principal = await ApiAuth.AuthorizeAsync(ctx, identities, ct);
            if (principal?.User is not { } user) return ApiAuth.Unauthorized();
            return Results.Ok(new
            {
                pages = ClassIslandSettingsCatalog.Pages,
                items = ClassIslandSettingsCatalog.Items,
            });
        });

        // 读取：该班教室电脑当前的 ClassIsland 设置（仅目录内的项）。
        app.MapGet("/api/classes/{classId:guid}/classisland-settings", async (Guid classId, HttpContext ctx,
            PeerRegistry peers, IdentityCoordinator identities, ClassAccessService access, CancellationToken ct) =>
        {
            var principal = await ApiAuth.AuthorizeAsync(ctx, identities, ct);
            if (principal?.User is not { } user) return ApiAuth.Unauthorized();
            if (await PermissionsAsync(user, classId, access, ct) is not { } permissions ||
                !permissions.HasFlag(UserPermissions.ControlClassIsland))
                return ApiAuth.Forbidden("没有该班级的 ClassIsland 控制权限");
            var result = await peers.SendCommandAndWaitAsync(
                new CommandMessage
                {
                    Command = CommandKind.ReadClassIslandSettings,
                    ClassId = classId,
                    RequestedBy = user.WithPermissions(permissions),
                },
                classId, CommandTimeout, ct);
            if (!result.Success) return Results.Json(result, statusCode: ScreenshotEndpoints.Status(result));
            var snapshot = TryDeserialize<ClassIslandSettingsSnapshot>(result.Data);
            if (snapshot?.Values is null)
                return ApiAuth.Error(ApiErrorCodes.InternalError, "设备返回的设置数据无效",
                    StatusCodes.Status422UnprocessableEntity);
            return Results.Ok(new { classId, values = snapshot.Values });
        });

        // 部分写入：values 只包含本次要修改的项，未出现的设置保持设备当前值。
        app.MapPut("/api/classes/{classId:guid}/classisland-settings", async (Guid classId, HttpContext ctx,
            PeerRegistry peers, IdentityCoordinator identities, ClassAccessService access, CancellationToken ct) =>
        {
            var principal = await ApiAuth.AuthorizeAsync(ctx, identities, ct);
            if (principal?.User is not { } user) return ApiAuth.Unauthorized();
            if (await PermissionsAsync(user, classId, access, ct) is not { } permissions ||
                !permissions.HasFlag(UserPermissions.ControlClassIsland))
                return ApiAuth.Forbidden("没有该班级的 ClassIsland 控制权限");
            ClassIslandSettingsRequest? request;
            try
            {
                request = await ctx.Request.ReadFromJsonAsync<ClassIslandSettingsRequest>(ct);
            }
            catch (System.Text.Json.JsonException)
            {
                return ApiAuth.Error(ApiErrorCodes.InvalidRequest, "设置数据格式无效", StatusCodes.Status400BadRequest);
            }
            var values = request?.Values;
            if (values is null || values.Count == 0)
                return ApiAuth.Error(ApiErrorCodes.InvalidRequest, "没有要修改的设置项", StatusCodes.Status400BadRequest);
            if (ClassIslandSettingsCatalog.ValidatePatch(values) is { } invalid)
                return ApiAuth.Error(ApiErrorCodes.InvalidRequest, invalid, StatusCodes.Status400BadRequest);
            var result = await peers.SendCommandAndWaitAsync(
                new CommandMessage
                {
                    Command = CommandKind.SetClassIslandSettings,
                    ClassIslandSettings = new ClassIslandSettingsRequest { Values = values },
                    ClassId = classId,
                    RequestedBy = user.WithPermissions(permissions),
                },
                classId, CommandTimeout, ct);
            return result.Success
                ? Results.Ok(new { classId, message = result.Message })
                : Results.Json(result, statusCode: ScreenshotEndpoints.Status(result));
        });
    }

    /// <summary>用户在该班的有效权限；无权访问该班时返回 null。</summary>
    private static async Task<UserPermissions?> PermissionsAsync(
        UserProfile user, Guid classId, ClassAccessService access, CancellationToken ct)
    {
        if (user.Role == UserRole.Admin) return UserPermissions.All;
        var classes = await access.GetAccessibleClassesAsync(user.Id, user.Role, ct);
        return classes.FirstOrDefault(x => x.Id == classId)?.Permissions;
    }

    private static T? TryDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json, JsonDefaults.Options);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
