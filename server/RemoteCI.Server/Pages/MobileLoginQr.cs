using Microsoft.AspNetCore.Mvc;
using QRCoder;
using RemoteCI.Server.Services;

namespace RemoteCI.Server.Pages;

/// <summary>_MobileLoginQrCard 分部视图的输入：二维码里写入的服务器地址与当前登录 ID。</summary>
public sealed record MobileLoginQrCardModel(string ServerUrl, string Username);

/// <summary>
/// 手机扫码登录二维码：概览页与个人账号页共用。二维码内含服务器地址、当前登录 ID 与一次性票据，
/// 安卓版扫码后直接登录当前 WebUI 账号。票据 5 分钟内有效且只能使用一次。
/// </summary>
public static class MobileLoginQr
{
    /// <summary>二维码服务器地址：系统设置“登录与访问”中指定的地址优先，否则使用当前访问地址。</summary>
    public static async Task<string> ResolveServerUrlAsync(MobileLoginSettings mobileLogin, HttpRequest request, CancellationToken ct) =>
        await mobileLogin.GetServerUrlAsync(ct) ?? $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');

    public static async Task<JsonResult> CreateAsync(
        IdentityCoordinator identities, MobileLoginSettings mobileLogin, HttpRequest request, Guid userId, CancellationToken ct)
    {
        var serverUrl = await ResolveServerUrlAsync(mobileLogin, request, ct);
        var ticket = await identities.CreateMobileLoginTicketAsync(userId, ct);
        var payload = MobileLoginSettings.BuildLoginQrPayload(serverUrl, ticket.Username, ticket.Ticket);
        using var qrData = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var renderer = new SvgQRCode(qrData);
        return new JsonResult(new
        {
            svg = renderer.GetGraphic(4),
            serverUrl,
            expiresInSeconds = (int)IdentityCoordinator.MobileLoginTicketLifetime.TotalSeconds,
        });
    }
}
