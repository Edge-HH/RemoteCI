using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using QRCoder;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Pages;

[Authorize]
public sealed class IndexModel(
    UserManager<AppUser> users, PeerRegistry peers, IStateStore state, IdentityCoordinator identities, MobileLoginSettings mobileLogin)
    : WebPageModel(users)
{
    public bool PluginOnline => peers.HasPluginFor(CurrentClassId);
    public int WatchConnections { get; private set; }
    public int AccountCount { get; private set; }
    public ClassStateSnapshot? Snapshot { get; private set; }
    public ScheduleBundle? Schedule { get; private set; }
    public string? PairCode { get; private set; }
    public string MobileLoginUrl { get; private set; } = string.Empty;
    public IReadOnlyList<PluginCredentialInfo> PluginCredentials { get; private set; } = [];
    public IReadOnlyList<PeerCapabilityDiagnostic> CapabilityDiagnostics { get; private set; } = [];
    public PluginProtocolMismatch? PluginProtocolMismatch => peers.LatestPluginProtocolMismatch;
    public bool IsAdmin => CurrentUser.Role == UserRole.Admin;
    public string ServerVersion => AppVersion.Version;
    public bool Supports(string capability) => !PluginOnline || peers.PrimaryPluginSupports(CurrentClassId, capability);
    public string FormatCapabilities(IReadOnlyList<string> capabilities) => string.Join(
        "、", capabilities.Select(capability => $"{capability}（{RemoteCiCapabilities.ChineseName(capability)}）"));

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        // 系统管理员首次进入、还没有任何班级时，先到初始化向导新建第一个班级。
        if (IsAdmin && AccessibleClasses.Count == 0) return RedirectToPage("/Setup");
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostRevokeCredentialAsync(Guid id, CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        if (CurrentUser.Role != UserRole.Admin)
        {
            TempData["Error"] = "仅管理员可吊销插件凭据。";
            return RedirectToPage();
        }
        try
        {
            await identities.RevokePluginCredentialAsync(id, ct);
            await peers.DisconnectPluginCredentialAsync(id, ct);
            TempData["Message"] = "插件凭据已吊销，对应插件已断开，需用新配对码重新配对。";
        }
        catch (IdentityOperationException ex) { TempData["Error"] = ex.Message; }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRetryConnectionAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        // 面向当前班级的连接检测：定向重新下发该班的授权镜像。
        var sync = await identities.CreateSyncAsync(CurrentClassId, ct);
        var connected = await peers.SendToPluginAsync(CurrentClassId, Envelope.AccountSync(sync), ct);
        TempData[connected ? "Message" : "Error"] = connected
            ? "插件连接检测成功，账号与权限已重新同步。"
            : "插件仍未连接，ClassIsland 插件会每 5 秒自动重试，请检查插件设置与服务地址。";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPairCodeAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi | UserPermissions.ManageUsers) is { } denied) return denied;
        // 配对码绑定当前班级；插件配对后归属该班，只能收发该班的命令与数据。
        if (CurrentClass is null)
        {
            TempData["Error"] = "请先新建班级，再生成配对码。";
            return RedirectToPage();
        }
        PairCode = await identities.CreatePluginPairingCodeAsync(CurrentClassId, ct);
        await LoadAsync(ct);
        return Page();
    }

    /// <summary>
    /// 按需生成手机扫码登录二维码：内含服务器地址、当前登录 ID 与一次性票据，
    /// 安卓版扫码后直接登录当前 WebUI 账号。票据 5 分钟内有效且只能使用一次。
    /// </summary>
    public async Task<IActionResult> OnPostMobileLoginQrAsync(CancellationToken ct)
    {
        if (await RequireAsync(UserPermissions.AccessWebUi) is { } denied) return denied;
        var serverUrl = await ResolveMobileServerUrlAsync(ct);
        var ticket = await identities.CreateMobileLoginTicketAsync(CurrentUser.Id, ct);
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

    /// <summary>二维码服务器地址：系统配置中指定的地址优先，否则使用当前访问地址。</summary>
    private async Task<string> ResolveMobileServerUrlAsync(CancellationToken ct) =>
        await mobileLogin.GetServerUrlAsync(ct) ?? $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');

    private async Task LoadAsync(CancellationToken ct)
    {
        MobileLoginUrl = await ResolveMobileServerUrlAsync(ct);
        WatchConnections = peers.WatchCount;
        AccountCount = (await identities.ListUsersAsync(ct)).Count;
        Snapshot = state.GetLatestSnapshot(CurrentClassId);
        Schedule = state.GetLatestSchedule(CurrentClassId);
        if (CurrentUser.Role == UserRole.Admin)
        {
            PluginCredentials = await identities.ListPluginCredentialsAsync(ct);
            CapabilityDiagnostics = peers.GetCapabilityDiagnostics(CurrentClassId);
        }
    }
}
