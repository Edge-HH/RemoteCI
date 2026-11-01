using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server;

public sealed record ProfileSummary(
    Guid Id, string Name, Guid? ClassId, Guid? SourceTemplateId, long Revision, DateTimeOffset UpdatedAt,
    int TimeLayoutCount, int ClassPlanCount, int SubjectCount)
{
    public static ProfileSummary From(StoredProfileDto row)
    {
        var preview = ProfileLibraryService.Preview(row.ProfileJson);
        return new(row.Id, row.Name, row.ClassId, row.SourceTemplateId, row.Revision, row.UpdatedAt,
            preview.TimeLayoutCount, preview.ClassPlanCount, preview.SubjectCount);
    }
}

public sealed record ProfilePreviewBody(string ProfileJson);
public sealed record ProfileSaveBody(List<ProfileSaveItem> Items);
public sealed record ProfileCopyBody(long Revision, string? Name);

/// <summary>
/// 档案库的 REST 接口，与 WebUI 档案页共用 ProfileLibraryService / ProfileDispatchService，
/// 权限、修订号与下发目标的校验全部在服务层完成，API 不另开捷径。
/// </summary>
public static class ProfileEndpoints
{
    // 与 WebUI 档案页一致：单份档案最多 5 MB，批量保存最多 100 份。
    private const long MaxRequestBytes = 64 * 1024 * 1024;

    public static void MapProfileEndpoints(this WebApplication app)
    {
        var profiles = app.MapGroup("/api/profiles");
        MapHistoryEndpoints(profiles);

        // 不带 classId：管理员得到全部档案，班主任得到自己可管理班级的档案。列表不含完整 JSON。
        profiles.MapGet("", (Guid? classId, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            var library = services.GetRequiredService<ProfileLibraryService>();
            var rows = classId is { } id ? await library.ListAsync(actor, id, ct) : await library.ListManageableAsync(actor, ct);
            return Results.Ok(rows.Select(ProfileSummary.From));
        }));

        profiles.MapGet("/{id:guid}", (Guid id, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
            Results.Ok(await services.GetRequiredService<ProfileLibraryService>().GetAsync(actor, id, ct: ct))));

        profiles.MapPost("/preview", (ProfilePreviewBody body, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, (_, _) =>
        {
            var json = ProfileDocument.Serialize(ProfileDocument.Parse(body.ProfileJson));
            return Task.FromResult(Results.Ok(new { preview = ProfileLibraryService.Preview(json), profileJson = json }));
        })).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));

        profiles.MapPut("", (ProfileSaveBody body, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
            Results.Ok(await services.GetRequiredService<ProfileLibraryService>().SaveAsync(actor, body.Items ?? [], ct: ct))))
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));

        profiles.MapPost("/{id:guid}/copy", (Guid id, ProfileCopyBody body, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
            Results.Ok(await services.GetRequiredService<ProfileLibraryService>().CopyAsync(
                actor, new ProfileIdRequest { Id = id, Revision = body.Revision, Name = body.Name }, ct))));

        profiles.MapDelete("/{id:guid}", (Guid id, long revision, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            await services.GetRequiredService<ProfileLibraryService>().DeleteAsync(actor, new ProfileIdRequest { Id = id, Revision = revision }, ct: ct);
            return Results.Ok(new { success = true, message = "服务端档案已删除，设备档案不受影响。" });
        }));

        // 从各班在线设备读取当前档案；结果只返回给调用方，不写入档案库，保存仍走 PUT /api/profiles。
        profiles.MapPost("/collect", (ProfileCollectRequest body, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            var results = await services.GetRequiredService<ProfileDispatchService>().CollectAsync(actor, body, ct: ct);
            return Results.Ok(new { success = results.Any(x => x.Success), results, message = ProfileCollectResult.Summary(results) });
        }));

        // 只下发已保存的修订版本；应用方式、类别与目标都必须显式给出，与 WebUI 的确认步骤一致。
        profiles.MapPost("/apply", (ProfileDispatchRequest body, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            var results = await services.GetRequiredService<ProfileDispatchService>().ApplyAsync(actor, body, ct: ct);
            return Results.Ok(new
            {
                success = results.Any(x => x.Success),
                results,
                message = $"完成 {results.Count(x => x.Success)} 台，失败 {results.Count(x => !x.Success)} 台。",
            });
        }));
    }

    private static void MapHistoryEndpoints(RouteGroupBuilder profiles)
    {
        profiles.MapGet("/{id:guid}/versions", (Guid id, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
            Results.Ok(await services.GetRequiredService<ProfileLibraryService>().ListVersionsAsync(actor, id, ct: ct))));

        profiles.MapGet("/{id:guid}/versions/{revision:long}/diff", (Guid id, long revision, string? against, HttpContext ctx, CancellationToken ct) =>
            RunAsync(ctx, ct, async (actor, services) => Results.Ok(await services.GetRequiredService<ProfileLibraryService>()
                .DiffVersionAsync(actor, id, revision, string.Equals(against, "current", StringComparison.OrdinalIgnoreCase), ct: ct))));

        // 恢复历史版本：另存为新修订，请求体的 revision 是调用方看到的当前修订号。
        profiles.MapPost("/{id:guid}/versions/{revision:long}/restore", (Guid id, long revision, ProfileIdRequest body, HttpContext ctx, CancellationToken ct) =>
            RunAsync(ctx, ct, async (actor, services) => Results.Ok(await services.GetRequiredService<ProfileLibraryService>()
                .RestoreVersionAsync(actor, id, revision, body.Revision, ct: ct))));

        // 档案差异检查：读取所选班级教室电脑的当前档案，与服务端班级档案比较。
        profiles.MapPost("/drift", (ProfileCollectRequest body, HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
            Results.Ok(await services.GetRequiredService<ProfileDriftService>().CheckAsync(actor, body.ClassIds ?? [], ct: ct))));

        profiles.MapGet("/drift", (HttpContext ctx, CancellationToken ct) => RunAsync(ctx, ct, async (actor, services) =>
        {
            var library = services.GetRequiredService<ProfileLibraryService>();
            var latest = await services.GetRequiredService<ProfileDriftService>().LatestAsync(ct: ct);
            var visible = new List<ProfileDriftView>();
            foreach (var view in latest.Values)
                if (await library.CanManageClassAsync(actor, view.ClassId, ct)) visible.Add(view);
            return Results.Ok(visible.OrderBy(x => x.ClassName, StringComparer.CurrentCulture));
        }));
    }

    private static async Task<IResult> RunAsync(
        HttpContext ctx, CancellationToken ct, Func<AppUser, IServiceProvider, Task<IResult>> action)
    {
        var services = ctx.RequestServices;
        var principal = await ApiAuth.AuthorizeAsync(ctx, services.GetRequiredService<IdentityCoordinator>(), ct);
        if (principal?.User is null) return ApiAuth.Unauthorized();
        // 连同角色一并加载：“档案库”系统权限按全局角色默认值与个人调整计算。
        var actor = await services.GetRequiredService<UserManager<AppUser>>().Users
            .Include(x => x.RoleDefinition)
            .SingleOrDefaultAsync(x => x.Id == principal.User.Id, ct);
        if (actor is null) return ApiAuth.Unauthorized();
        try { return await action(actor, services); }
        catch (UnauthorizedAccessException ex) { return ApiAuth.Forbidden(ex.Message); }
        catch (KeyNotFoundException ex) { return ApiAuth.Error(ApiErrorCodes.NotFound, ex.Message, StatusCodes.Status404NotFound); }
        catch (ProfileRevisionException ex) { return ApiAuth.Error(ApiErrorCodes.ProfileStale, ex.Message, StatusCodes.Status409Conflict); }
        catch (ArgumentException ex) { return ApiAuth.Error(ApiErrorCodes.InvalidRequest, ex.Message, StatusCodes.Status400BadRequest); }
        catch (JsonException) { return ApiAuth.Error(ApiErrorCodes.InvalidRequest, "档案 JSON 格式无效。", StatusCodes.Status400BadRequest); }
    }
}
