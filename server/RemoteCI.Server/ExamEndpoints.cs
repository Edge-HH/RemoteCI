using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server;

/// <summary>
/// 考试端绑定。票据由已配对的教室插件领取，ExamAware 插件凭票据或仍有效的班级配对码登记。
/// 登记不会新建插件凭据，也不会挤掉 ClassIsland 上的 RemoteCI 连接。
/// </summary>
public static class ExamEndpoints
{
    public static void MapExamEndpoints(this WebApplication app)
    {
        app.MapPost("/api/exam/tickets", async (HttpContext ctx, IdentityCoordinator identities, ExamRegistry exams,
            AppDbContext db, CancellationToken ct) =>
        {
            var token = Bearer(ctx);
            var principal = token.Length == 0 ? null : await identities.ValidatePluginTokenAsync(token, ct);
            if (principal is null) return ApiAuth.Unauthorized();
            if (principal.ClassId is not { } classId)
                return ApiAuth.Error(ApiErrorCodes.InvalidRequest, "设备尚未分配到班级，不能领取考试绑定票据", StatusCodes.Status409Conflict);
            var classroom = await db.Classrooms.FindAsync([classId], ct);
            if (classroom is null)
                return ApiAuth.Error(ApiErrorCodes.NotFound, "班级不存在", StatusCodes.Status404NotFound);
            var (ticket, expires) = exams.IssueTicket(classId, classroom.Name);
            return Results.Ok(new ExamTicketResponse
            {
                Ticket = ticket,
                ClassId = classId,
                ClassName = classroom.Name,
                ExpiresAt = expires,
            });
        }).RequireRateLimiting("auth");

        app.MapPost("/api/exam/bind", async (ExamBindRequest body, IdentityCoordinator identities, ExamRegistry exams,
            CancellationToken ct) =>
        {
            (Guid Id, string Name)? pair = string.IsNullOrWhiteSpace(body.PairCode)
                ? null
                : await identities.FindClassByActivePairCodeAsync(body.PairCode, ct);
            var bound = exams.Bind(body.Ticket, pair, body.DeviceName);
            return bound is null
                ? ApiAuth.Error(ApiErrorCodes.Unauthorized, "连接信息无效或已过期", StatusCodes.Status401Unauthorized)
                : Results.Ok(bound);
        }).RequireRateLimiting("auth");

        app.MapGet("/api/exam/jobs", (HttpContext ctx, ExamRegistry exams) =>
        {
            var agent = exams.Authenticate(Bearer(ctx));
            if (agent is null) return ApiAuth.Unauthorized();
            return Results.Ok(new { jobs = exams.OpenJobs(agent.ClassId) });
        });

        app.MapPost("/api/exam/jobs/{id:guid}/ack", (Guid id, ExamJobAck body, HttpContext ctx, ExamRegistry exams) =>
        {
            var agent = exams.Authenticate(Bearer(ctx));
            if (agent is null) return ApiAuth.Unauthorized();
            return exams.Ack(agent.ClassId, id, body.State, body.Message)
                ? Results.NoContent()
                : ApiAuth.Error(ApiErrorCodes.InvalidRequest, "任务不存在或状态无效", StatusCodes.Status400BadRequest);
        });
    }

    private static string Bearer(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        return header.StartsWith($"{Protocol.BearerScheme} ", StringComparison.OrdinalIgnoreCase)
            ? header[(Protocol.BearerScheme.Length + 1)..].Trim()
            : string.Empty;
    }
}
