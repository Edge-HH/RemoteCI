using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server;

/// <summary>
/// 集控运维类 REST 接口（操作记录等），与 WebUI 对应页面共用服务层和可见范围：
/// 系统管理员查看全部，班主任只看自己担任班主任的班级。
/// </summary>
public static class OperationsEndpoints
{
    public static void MapOperationsEndpoints(this WebApplication app)
    {
        app.MapGet("/api/audit", (
            DateTimeOffset? from, DateTimeOffset? to, Guid? classId, Guid? userId, int? command, bool? success,
            long? before, int? limit, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            var audit = services.GetRequiredService<CommandAuditQueryService>();
            var scope = await audit.GetScopeAsync(actor, ct);
            if (scope is { Count: 0 }) return ApiAuth.Forbidden("只有系统管理员和班主任可以查看操作记录");
            return Results.Ok(await audit.QueryAsync(
                scope, new CommandAuditFilter(from, to, classId, userId, command, success, before, limit ?? 50), ct));
        }));

        app.MapGet("/api/audit/export", (
            DateTimeOffset? from, DateTimeOffset? to, Guid? classId, Guid? userId, int? command, bool? success,
            HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            var audit = services.GetRequiredService<CommandAuditQueryService>();
            var scope = await audit.GetScopeAsync(actor, ct);
            if (scope is { Count: 0 }) return ApiAuth.Forbidden("只有系统管理员和班主任可以导出操作记录");
            var bytes = await audit.ExportAsync(scope, new CommandAuditFilter(from, to, classId, userId, command, success), ct);
            return Results.File(bytes, ExcelContentType, $"操作记录-{DateTime.Now:yyyyMMdd-HHmm}.xlsx");
        }));

        MapDeviceEndpoints(app);
        MapAutomationEndpoints(app);
        MapLessonStatsEndpoints(app);

        // 聊天机器人卡片按钮：为当前账号签发一次性确认链接（同意/拒绝换课、撤回强制换课）。
        app.MapPost("/api/me/action-links", (ActionLinkRequest body, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            var baseUrl = $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}";
            try { return Results.Ok(await services.GetRequiredService<ActionLinkService>().CreateAsync(actor.Id, body, baseUrl, ct)); }
            catch (SwapOperationException ex) { return ApiAuth.Error(ex.Code, ex.Message, ex.Status); }
        }));
    }

    private static void MapLessonStatsEndpoints(WebApplication app)
    {
        // 课时统计：系统管理员或“查看全部课时统计”看全部班级，其他账号看自己有“课时统计”权限的班级；
        // from/to 为教室端本地日期，省略时为本月。
        app.MapGet("/api/lesson-stats", (DateOnly? from, DateOnly? to, Guid? classId, string? teacher, bool? includeProfile,
            HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            var scope = await services.GetRequiredService<ClassAccessService>().GetPermissionScopeAsync(
                actor.Id, actor.Role, UserPermissions.ViewLessonStats, ManagementPermissions.ViewAllLessonStats, ct);
            if (scope is { Count: 0 }) return ApiAuth.Forbidden("没有查看课时统计的权限");
            var (start, end) = LessonStatsRange(from, to);
            return Results.Ok(await services.GetRequiredService<LessonStatsService>()
                .GetAsync(scope, start, end, classId, NullIfBlank(teacher), includeProfile == true, ct));
        }));

        app.MapGet("/api/lesson-stats/export", (DateOnly? from, DateOnly? to, Guid? classId, string? teacher, bool? includeProfile,
            HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            var scope = await services.GetRequiredService<ClassAccessService>().GetPermissionScopeAsync(
                actor.Id, actor.Role, UserPermissions.ViewLessonStats, ManagementPermissions.ViewAllLessonStats, ct);
            if (scope is { Count: 0 }) return ApiAuth.Forbidden("没有导出课时统计的权限");
            var (start, end) = LessonStatsRange(from, to);
            var bytes = await services.GetRequiredService<LessonStatsService>()
                .ExportAsync(scope, start, end, classId, NullIfBlank(teacher), includeProfile == true, ct);
            return Results.File(bytes, ExcelContentType, $"课时统计-{start:yyyyMMdd}-{end:yyyyMMdd}.xlsx");
        }));

        // 我的课时：按本人姓名（显示名）在全部班级的台账中统计，不受班级成员关系限制。
        app.MapGet("/api/me/lesson-stats", (DateOnly? from, DateOnly? to, HttpContext ctx, CancellationToken ct) =>
            RunAsync(ctx, ct, async (actor, services) =>
            {
                var (start, end) = LessonStatsRange(from, to);
                var report = await services.GetRequiredService<LessonStatsService>()
                    .GetAsync(null, start, end, teacher: actor.DisplayName, ct: ct);
                return Results.Ok(report);
            }));
    }

    /// <summary>省略日期时取服务端本地时间的本月 1 日至今天。</summary>
    public static (DateOnly From, DateOnly To) LessonStatsRange(DateOnly? from, DateOnly? to)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var start = from ?? new DateOnly(today.Year, today.Month, 1);
        return (start, to ?? today);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void MapAutomationEndpoints(WebApplication app)
    {
        var rules = app.MapGroup("/api/automation");
        rules.MapGet("/rules", (HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            AutomationRuleService.RequireAdmin(actor);
            return Results.Ok(await services.GetRequiredService<AutomationRuleService>().ListAsync(ct));
        }));
        rules.MapGet("/rules/{id:guid}", (Guid id, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            AutomationRuleService.RequireAdmin(actor);
            return Results.Ok(await services.GetRequiredService<AutomationRuleService>().GetAsync(id, ct));
        }));
        rules.MapPost("/rules", (AutomationRuleDto body, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            body.Id = null;
            return Results.Ok(await services.GetRequiredService<AutomationRuleService>().SaveAsync(actor, body, ct));
        }));
        rules.MapPut("/rules/{id:guid}", (Guid id, AutomationRuleDto body, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            body.Id = id;
            return Results.Ok(await services.GetRequiredService<AutomationRuleService>().SaveAsync(actor, body, ct));
        }));
        rules.MapDelete("/rules/{id:guid}", (Guid id, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            await services.GetRequiredService<AutomationRuleService>().DeleteAsync(actor, id, ct);
            return Results.NoContent();
        }));
        // 立即运行一次：对全部目标班级执行，忽略触发条件；同步等待各班结果。
        rules.MapPost("/rules/{id:guid}/run", (Guid id, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
            Results.Ok(await services.GetRequiredService<AutomationEngine>().RunNowAsync(actor, id, DateTimeOffset.UtcNow, ct))));
        rules.MapGet("/runs", (Guid? ruleId, int? limit, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            AutomationRuleService.RequireAdmin(actor);
            return Results.Ok(await services.GetRequiredService<AutomationRuleService>().ListRunsAsync(ruleId, limit ?? 50, ct));
        }));
    }

    private static void MapDeviceEndpoints(WebApplication app)
    {
        // 设备状态：在线、版本、当前课程与离线时长；有“设备状态”系统权限看全部，
        // 其他账号看自己有“操作记录与设备状态”权限的班级。
        app.MapGet("/api/devices/health", (HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            var scope = await services.GetRequiredService<ClassAccessService>().GetPermissionScopeAsync(
                actor.Id, actor.Role, UserPermissions.ViewAuditLog, ManagementPermissions.ViewDevices, ct);
            if (scope is { Count: 0 }) return ApiAuth.Forbidden("没有查看设备状态的权限");
            var overview = await services.GetRequiredService<DeviceHealthService>().GetOverviewAsync(scope, ct);
            return Results.Ok(overview.Devices);
        }));

        // 应用守护：全局默认策略与单设备覆盖，只允许系统管理员读写；组织名在系统配置中设置，这里只读。
        app.MapGet("/api/settings/app-guard", (HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            RequireAdmin(actor, "没有查看应用守护设置的权限");
            return Results.Ok(await services.GetRequiredService<AppGuardService>().GetGlobalAsync(ct));
        }));
        app.MapPut("/api/settings/app-guard", (AppGuardPolicy body, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            RequireAdmin(actor, "没有修改应用守护设置的权限");
            return Results.Ok(await services.GetRequiredService<AppGuardService>().SetGlobalAsync(body, ct));
        }));
        app.MapGet("/api/devices/{credentialId:guid}/app-guard", (Guid credentialId, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            RequireAdmin(actor, "没有查看应用守护设置的权限");
            return Results.Ok(await services.GetRequiredService<AppGuardService>().GetDeviceAsync(credentialId, ct));
        }));
        app.MapPut("/api/devices/{credentialId:guid}/app-guard", (Guid credentialId, AppGuardPolicy body, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            RequireAdmin(actor, "没有修改应用守护设置的权限");
            return Results.Ok(await services.GetRequiredService<AppGuardService>().SetOverrideAsync(credentialId, body, ct));
        }));
        app.MapDelete("/api/devices/{credentialId:guid}/app-guard", (Guid credentialId, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            RequireAdmin(actor, "没有修改应用守护设置的权限");
            return Results.Ok(await services.GetRequiredService<AppGuardService>().ClearOverrideAsync(credentialId, ct));
        }));
        app.MapGet("/api/app-guard/events", (Guid? credentialId, int? limit, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            RequireAdmin(actor, "没有查看应用守护事件的权限");
            return Results.Ok(await services.GetRequiredService<AppGuardService>().GetRecentEventsAsync(credentialId, limit ?? 100, ct));
        }));
    }

    private static void RequireAdmin(AppUser actor, string message)
    {
        if (!AccountPermissions.HasManagement(actor, ManagementPermissions.ManageAppGuard))
            throw new UnauthorizedAccessException(message);
    }

    public const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static async Task<IResult> RunAsync(
        HttpContext ctx, CancellationToken ct, Func<AppUser, IServiceProvider, Task<IResult>> action)
    {
        var services = ctx.RequestServices;
        var principal = await ApiAuth.AuthorizeAsync(ctx, services.GetRequiredService<IdentityCoordinator>(), ct);
        if (principal?.User is null) return ApiAuth.Unauthorized();
        // 连同角色一并加载：系统管理权限按全局角色默认值与个人调整计算。
        var actor = await services.GetRequiredService<UserManager<AppUser>>().Users
            .Include(x => x.RoleDefinition)
            .SingleOrDefaultAsync(x => x.Id == principal.User.Id, ct);
        if (actor is null || !actor.Enabled) return ApiAuth.Unauthorized();
        try { return await action(actor, services); }
        catch (UnauthorizedAccessException ex) { return ApiAuth.Forbidden(ex.Message); }
        catch (KeyNotFoundException ex) { return ApiAuth.Error(ApiErrorCodes.NotFound, ex.Message, StatusCodes.Status404NotFound); }
        catch (ArgumentException ex) { return ApiAuth.Error(ApiErrorCodes.InvalidRequest, ex.Message, StatusCodes.Status400BadRequest); }
    }
}
