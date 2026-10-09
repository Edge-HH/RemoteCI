using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server;

/// <summary>
/// 独立端点文件共用的鉴权与错误响应；与 Program.cs 中的同名本地函数一致
/// （顶级语句里的本地函数无法跨文件复用）。
/// </summary>
internal static class ApiAuth
{
    public static async Task<AuthPrincipal?> AuthorizeAsync(HttpContext ctx, IdentityCoordinator identities, CancellationToken ct)
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

    public static IResult Error(string code, string message, int status) =>
        Results.Json(new ApiError { Code = code, Message = message }, statusCode: status);

    public static IResult Unauthorized() =>
        Error(ApiErrorCodes.Unauthorized, "未登录或登录已失效", StatusCodes.Status401Unauthorized);

    public static IResult Forbidden(string message = "权限不足") =>
        Error(ApiErrorCodes.Forbidden, message, StatusCodes.Status403Forbidden);
}
