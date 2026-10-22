using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server;

/// <summary>
/// 文件中转接口：先上传（请求体即文件内容），再发送到班级，之后按传输编号查询每台设备的接收进度。
/// 插件凭下载凭据从 <c>GET /api/plugin/files/{id}</c> 分段下载（支持 Range 断点续传）。
/// </summary>
public static class FileTransferEndpoints
{
    public const string TransferKeyHeader = "X-RemoteCI-Transfer-Key";

    public static void MapFileTransferEndpoints(this WebApplication app)
    {
        app.MapPost("/api/files", async (string? name, HttpContext ctx, FileTransferService transfers,
            IdentityCoordinator identities, ClassAccessService access, CancellationToken ct) =>
        {
            var principal = await ApiAuth.AuthorizeAsync(ctx, identities, ct);
            if (principal?.User is not { } user) return ApiAuth.Unauthorized();
            if (!await CanSendFilesAnywhereAsync(user, access, ct))
                return ApiAuth.Forbidden("没有文件分发权限");
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = transfers.MaxFileBytes + 1;
            if (ctx.Request.ContentLength > transfers.MaxFileBytes)
                return ApiAuth.Error(ApiErrorCodes.InvalidRequest,
                    $"文件不能超过 {FileTransferService.FormatSize(transfers.MaxFileBytes)}", StatusCodes.Status413PayloadTooLarge);
            var fileName = name;
            if (string.IsNullOrWhiteSpace(fileName) && ctx.Request.Headers["X-File-Name"].ToString() is { Length: > 0 } header)
                fileName = Uri.UnescapeDataString(header);
            try
            {
                var record = await transfers.CreateAsync(ctx.Request.Body, fileName, user, ct);
                return Results.Json(transfers.ToView(record), statusCode: StatusCodes.Status201Created);
            }
            catch (FileTransferException ex) { return ApiAuth.Error(ex.Code, ex.Message, ex.Status); }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return ApiAuth.Error(ApiErrorCodes.InvalidRequest,
                    $"文件不能超过 {FileTransferService.FormatSize(transfers.MaxFileBytes)}", StatusCodes.Status413PayloadTooLarge);
            }
        });

        app.MapGet("/api/files/{id}", async (string id, HttpContext ctx, FileTransferService transfers,
            IdentityCoordinator identities, CancellationToken ct) =>
        {
            var principal = await ApiAuth.AuthorizeAsync(ctx, identities, ct);
            if (principal?.User is not { } user) return ApiAuth.Unauthorized();
            return transfers.Get(id) is { } record && FileTransferService.CanManage(record, user)
                ? Results.Ok(transfers.ToView(record))
                : NotFound();
        });

        app.MapDelete("/api/files/{id}", async (string id, HttpContext ctx, FileTransferService transfers,
            IdentityCoordinator identities, CancellationToken ct) =>
        {
            var principal = await ApiAuth.AuthorizeAsync(ctx, identities, ct);
            if (principal?.User is not { } user) return ApiAuth.Unauthorized();
            if (transfers.Get(id) is not { } record || !FileTransferService.CanManage(record, user)) return NotFound();
            transfers.Delete(record.Id);
            return Results.NoContent();
        });

        app.MapPost("/api/files/{id}/send", async (string id, FileSendBody body, HttpContext ctx,
            FileTransferService transfers, FileTransferDispatcher dispatcher, IdentityCoordinator identities,
            ClassAccessService access, PersonalScheduleService schedule, CancellationToken ct) =>
        {
            var principal = await ApiAuth.AuthorizeAsync(ctx, identities, ct);
            if (principal?.User is not { } user) return ApiAuth.Unauthorized();
            if (transfers.Get(id) is not { } record || !FileTransferService.CanManage(record, user)) return NotFound();
            if (!Enum.IsDefined(body.TargetFolder))
                return ApiAuth.Error(ApiErrorCodes.InvalidRequest, "目标文件夹无效", StatusCodes.Status400BadRequest);

            var classes = await access.GetAccessibleClassesAsync(user.Id, user.Role, user.GrantedPermissions, ct);
            var (targets, error) = await ResolveTargetsAsync(body, user, classes, schedule, ct);
            if (error is not null) return ApiAuth.Error(ApiErrorCodes.InvalidRequest, error, StatusCodes.Status400BadRequest);
            var permissions = classes.ToDictionary(x => x.Id, x => x.Permissions ?? UserPermissions.None);
            var denied = targets.Where(x => !permissions.GetValueOrDefault(x).HasFlag(UserPermissions.SendFiles)).ToList();
            if (denied.Count > 0)
                return ApiAuth.Forbidden($"没有向 {string.Join("、", denied.Select(x => classes.FirstOrDefault(c => c.Id == x)?.Name ?? x.ToString()))} 分发文件的权限");

            var view = await dispatcher.SendAsync(record, targets, [], [], body.TargetFolder, body.Overwrite,
                target => user.WithPermissions(permissions.GetValueOrDefault(target.ClassId)), ct);
            return Results.Ok(view);
        });

        // 插件下载：插件凭据 + 命令中下发的下载凭据，二者缺一不可；支持 Range 断点续传。
        app.MapGet("/api/plugin/files/{id}", async (string id, HttpContext ctx, FileTransferService transfers,
            IdentityCoordinator identities, CancellationToken ct) =>
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            var token = header.StartsWith($"{Protocol.BearerScheme} ", StringComparison.OrdinalIgnoreCase)
                ? header[(Protocol.BearerScheme.Length + 1)..].Trim()
                : string.Empty;
            var principal = token.Length == 0 ? null : await identities.ValidateAnyTokenAsync(token, ct);
            if (principal is null) return ApiAuth.Unauthorized();
            if (transfers.Get(id) is not { } record ||
                !transfers.CanDownload(record, principal, ctx.Request.Headers[TransferKeyHeader].ToString()))
                return NotFound();
            return Results.File(transfers.ContentPath(record.Id), "application/octet-stream",
                enableRangeProcessing: true);
        });
    }

    /// <summary>上传前的粗检：系统管理员，或至少在一个班级拥有 SendFiles。发送时再按班级逐个复核。</summary>
    private static async Task<bool> CanSendFilesAnywhereAsync(UserProfile user, ClassAccessService access, CancellationToken ct) =>
        user.Role == UserRole.Admin ||
        (await access.GetAccessibleClassesAsync(user.Id, user.Role, user.GrantedPermissions, ct))
            .Any(x => x.Permissions?.HasFlag(UserPermissions.SendFiles) == true);

    private static async Task<(IReadOnlyList<Guid> Targets, string? Error)> ResolveTargetsAsync(
        FileSendBody body, UserProfile user, IReadOnlyList<ClassSummary> classes,
        PersonalScheduleService schedule, CancellationToken ct)
    {
        var targets = new List<Guid>();
        foreach (var classId in body.ClassIds ?? [])
        {
            if (classes.All(x => x.Id != classId)) return ([], $"班级 {classId} 不存在或无权访问");
            targets.Add(classId);
        }
        foreach (var raw in body.ClassNames ?? [])
        {
            var name = raw.Trim();
            if (name.Length == 0) continue;
            var exact = classes.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            var matches = exact.Count > 0 ? exact : classes.Where(x => x.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) return ([], $"没有找到班级“{name}”");
            if (matches.Count > 1) return ([], $"“{name}”匹配到多个班级：{string.Join("、", matches.Select(x => x.Name))}，请写完整班名");
            targets.Add(matches[0].Id);
        }
        if (body.NextClass)
        {
            var next = await schedule.NextAsync(user.Id,
                user.RoleKind is { } kind && AccountRole.HasPersonalSchedule((AccountRoleKind)kind) ? user.DisplayName : null,
                DateTimeOffset.UtcNow, ClassClock.Valid(body.TimeZoneOffsetMinutes), ct);
            // 正在上课时“下一个要上课的班”仍指接下来那节；没有后续课程时才退回当前这节。
            var slot = next.Next ?? next.Current;
            if (slot is null || slot.ClassId == Guid.Empty) return ([], "没有找到你接下来要上课的班级");
            targets.Add(slot.ClassId);
        }
        targets = targets.Distinct().ToList();
        return targets.Count == 0 ? ([], "请指定目标班级（classIds、classNames 或 nextClass）") : (targets, null);
    }

    private static IResult NotFound() =>
        ApiAuth.Error(ApiErrorCodes.NotFound, "文件不存在或已过期", StatusCodes.Status404NotFound);
}

public sealed class FileSendBody
{
    [JsonPropertyName("classIds")] public List<Guid>? ClassIds { get; set; }

    /// <summary>班级名称；完全匹配优先，否则要求片段唯一匹配。</summary>
    [JsonPropertyName("classNames")] public List<string>? ClassNames { get; set; }

    /// <summary>true 时加入当前账号“我的日程”中接下来要上课的班级。</summary>
    [JsonPropertyName("nextClass")] public bool NextClass { get; set; }

    [JsonPropertyName("timeZoneOffsetMinutes")] public int? TimeZoneOffsetMinutes { get; set; }

    [JsonPropertyName("targetFolder")] public FileTargetFolder TargetFolder { get; set; } = FileTargetFolder.Desktop;

    [JsonPropertyName("overwrite")] public bool Overwrite { get; set; }
}
