using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Net;
using RemoteCI.Server;
using RemoteCI.Server.Data;
using RemoteCI.Server.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using System.Threading.RateLimiting;

if (await ApplicationRestartCoordinator.TryRunHelperAsync(args)) return;
if (await UpdateInstaller.TryRunAsync(args)) return;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.SectionName));

builder.Services.AddDbContext<AppDbContext>((services, options) =>
{
    var configured = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value.DatabasePath;
    var environment = services.GetRequiredService<IHostEnvironment>();
    var path = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    options.UseSqlite($"Data Source={path}");
});
builder.Services.AddIdentity<AppUser, IdentityRole<Guid>>(options =>
    {
        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = false;
        options.Password.RequireLowercase = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireNonAlphanumeric = false;
        options.User.RequireUniqueEmail = false;
        options.Lockout.MaxFailedAccessAttempts = 8;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(10);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

// Cookie 加密密钥持久化到数据库同级目录：容器或服务账号重启后 WebUI 会话保持有效。
var serverSection = builder.Configuration.GetSection(ServerOptions.SectionName).Get<ServerOptions>() ?? new ServerOptions();
var dataDirectory = Path.IsPathRooted(serverSection.DatabasePath)
    ? Path.GetDirectoryName(Path.GetFullPath(serverSection.DatabasePath))!
    : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, Path.GetDirectoryName(serverSection.DatabasePath)!));
Directory.CreateDirectory(dataDirectory);
builder.Services.AddDataProtection()
    .SetApplicationName("RemoteCI.Server")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")));

// 登录/刷新/配对等认证端点按客户端 IP 限流，作为 Identity 锁定之外的第一道爆破防线。
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, context.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<ServerOptions>>().Value.AuthRateLimitPerMinute),
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
        }));
});
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "RemoteCI.Web";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/Login";
    options.AccessDeniedPath = "/Denied";
});
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Visitor");
    options.Conventions.AllowAnonymousToPage("/SetupPassword");
    options.Conventions.AllowAnonymousToPage("/WebLogin");
    options.Conventions.AllowAnonymousToPage("/StatusCode");
});
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IdentityCoordinator>();
builder.Services.AddScoped<AccountRoleService>();
builder.Services.AddScoped<ExtensionPolicyService>();
builder.Services.AddScoped<AuthorizationSyncService>();
builder.Services.AddScoped<ConfigurationArchiveService>();
builder.Services.AddScoped<SchedulePullSettings>();
builder.Services.AddScoped<HolidaySettingsStore>();
builder.Services.AddScoped<VisitorAccessSettings>();
builder.Services.AddScoped<LoginPageSettings>();
builder.Services.AddScoped<MobileLoginSettings>();
builder.Services.AddScoped<TeacherBindingService>();
builder.Services.AddScoped<ClassAccessService>();
builder.Services.AddScoped<ProfileLibraryService>();
builder.Services.AddScoped<ProfileDispatchService>();
builder.Services.AddScoped<ClassBroadcastService>();
 builder.Services.AddScoped<DeviceInventoryService>();
builder.Services.AddScoped<UserImportService>();
builder.Services.AddScoped<MemberExcelService>();
builder.Services.AddScoped<ClassExcelService>();
builder.Services.AddScoped<ClassSelfServiceSettings>();
builder.Services.AddScoped<ExtensionGroupService>();
builder.Services.AddScoped<PendingExtensionSettingsService>();
builder.Services.AddSingleton<ExtensionSettingsReplayService>();
builder.Services.AddScoped<UserNotificationService>();
builder.Services.AddScoped<ScheduleSwapService>();
builder.Services.AddSingleton<IScheduleCommandSender, PeerScheduleCommandSender>();
builder.Services.AddSingleton<WebPushSender>();
builder.Services.AddHttpClient(WebPushSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped(sp =>
{
    // 插件在线数来自单例连接注册表；scoped 服务通过回调取数，避免直接依赖单例链。
    var service = new ClassroomService(sp.GetRequiredService<AppDbContext>());
    var registry = sp.GetRequiredService<PeerRegistry>();
    service.OnlinePluginCounter = classId => registry.GetOnlinePluginConnections().Count(x => x.ClassId == classId);
    return service;
});
builder.Services.AddSingleton<LessonOverrideTable>();
builder.Services.AddSingleton<IStateStore>(sp => new StateStore(sp.GetRequiredService<LessonOverrideTable>()));
builder.Services.AddSingleton<PeerRegistry>();
builder.Services.AddSingleton<ScheduleSyncTaskTracker>();
builder.Services.AddSingleton<ScheduleSyncService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<HolidayClock>();
builder.Services.AddSingleton<HolidayCalendarService>();
builder.Services.AddHttpClient(HolidayCalendarService.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("RemoteCI-Server");
});
builder.Services.AddHostedService<HolidayCalendarWorker>();
builder.Services.AddHostedService<SchedulePullWorker>();
builder.Services.AddHostedService<PeerAuthorizationRefreshWorker>();
builder.Services.AddHostedService<AutomaticBackupWorker>();
builder.Services.AddHostedService<ScheduleSwapSweepWorker>();
builder.Services.AddSingleton(new UpdateService(args));

var app = builder.Build();
// 更新安装器启动本进程时会通过环境变量指定启动成功标记路径：
// 主机完成启动后写入标记，安装器据此健康检查，失败则回滚到旧版本。
var startupMarker = Environment.GetEnvironmentVariable(UpdateInstaller.StartupMarkerEnvVar);
if (!string.IsNullOrWhiteSpace(startupMarker))
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try { File.WriteAllText(startupMarker, DateTimeOffset.Now.ToString("O")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不出标记即视为启动失败，安装器会回滚。
        }
    });
}
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    // 显式声明只信任本机回环上的反向代理（框架默认值，此处固化意图）：
    // 公网直连客户端伪造的 X-Forwarded-Proto/For 会被忽略，无法绕过 HTTPS 重定向与 HSTS。
    KnownIPNetworks =
    {
        new System.Net.IPNetwork(IPAddress.Loopback, 8),
        new System.Net.IPNetwork(IPAddress.IPv6Loopback, 128),
    },
});
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
// 非法地址等空响应的错误状态渲染为 WebUI 状态页（带返回主页入口）；
// API 与 WebSocket 由客户端按状态码/JSON 处理，保持原样不插入 HTML。
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/ws"),
    branch => branch.UseStatusCodePagesWithReExecute("/status/{0}"));
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
// ping/pong 保活：30 秒一次，60 秒内无 pong 视为半开连接由底层中止，避免僵尸连接滞留注册表。
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30),
    KeepAliveTimeout = TimeSpan.FromSeconds(60),
});

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<IdentityCoordinator>().BootstrapAsync();
    // 换课产生的单节临时任课老师覆盖需要在接受连接前载入内存，首个课表推送即可正确叠加。
    await scope.ServiceProvider.GetRequiredService<ScheduleSwapService>().LoadOverridesAsync();
}

app.Map("/ws", async context =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("WebSocketHub");
    await WebSocketHub.HandleAsync(
        context,
        context.RequestServices.GetRequiredService<IdentityCoordinator>(),
        context.RequestServices.GetRequiredService<PeerRegistry>(),
        context.RequestServices.GetRequiredService<IStateStore>(),
        context.RequestServices.GetRequiredService<ExtensionPolicyService>(),
        context.RequestServices.GetRequiredService<AuthorizationSyncService>(),
        context.RequestServices.GetRequiredService<ScheduleSyncService>(),
        context.RequestServices.GetRequiredService<ClassAccessService>(),
        context.RequestServices.GetRequiredService<TeacherBindingService>(),
        logger);
});

app.MapPost("/api/plugin/pair", async (PairRequest request, IdentityCoordinator identities, CancellationToken ct) =>
{
    if (MissingFields(request.PairCode, request.Role) is { } bad) return bad;
    if (!string.Equals(request.Role, "plugin", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "此端点仅用于插件配对"));
    try { return Results.Ok(await identities.PairPluginAsync(request, ct)); }
    catch (IdentityOperationException ex) { return OperationError(ex); }
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/login", async (
    LoginRequest request, IdentityCoordinator identities, AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    if (MissingFields(request.Username) is { } bad) return bad;
    try
    {
        var response = await identities.LoginAsync(request, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.Ok(response);
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
}).RequireRateLimiting("auth");

// 手机扫码登录：WebUI 二维码携带一次性票据，安卓版凭票据直接登录二维码所属账号。
app.MapPost("/api/auth/mobile-login", async (
    MobileLoginRequest request, IdentityCoordinator identities, AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    if (MissingFields(request.Ticket) is { } bad) return bad;
    try
    {
        var response = await identities.RedeemMobileLoginTicketAsync(request, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.Ok(response);
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
}).RequireRateLimiting("auth");

// 客户端一键打开 WebUI：为当前设备会话的账号签发 1 分钟一次性票据，客户端用浏览器打开 /WebLogin?t=… 即自动登录。
app.MapPost("/api/auth/web-ticket", async (HttpContext ctx, IdentityCoordinator identities, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.IsApiKey) return Forbidden();
    try
    {
        var ticket = await identities.CreateWebLoginTicketAsync(principal.User.Id, ct);
        return Results.Ok(new
        {
            ticket = ticket.Ticket,
            path = $"/WebLogin?t={Uri.EscapeDataString(ticket.Ticket)}",
            expiresAt = ticket.ExpiresAt,
        });
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/refresh", async (
    RefreshSessionRequest request, IdentityCoordinator identities, AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    if (MissingFields(request.DeviceSecret) is { } bad) return bad;
    try
    {
        var response = await identities.RefreshAsync(request, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.Ok(response);
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
}).RequireRateLimiting("auth");

// 首登设置密码：批量导入的待激活账号凭一次性令牌补设密码后即可正常登录。
app.MapPost("/api/auth/setup-password", async (
    SetupPasswordRequest request, IdentityCoordinator identities, AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    if (MissingFields(request.Username, request.SetupToken, request.NewPassword) is { } bad) return bad;
    try
    {
        await identities.SetupPasswordAsync(request, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
}).RequireRateLimiting("auth");

app.MapPost("/api/auth/logout", async (
    HttpContext ctx, IdentityCoordinator identities, AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null || principal.DeviceSessionId is null) return Unauthorized();
    await identities.RevokeSessionAsync(principal.User.Id, principal.DeviceSessionId.Value, ct);
    await authorizationSync.SyncAsync(ct);
    return Results.NoContent();
});

app.MapGet("/api/me", async (HttpContext ctx, IdentityCoordinator identities, CancellationToken ct) =>
    await AuthorizeAsync(ctx, identities, ct) is { User: not null } principal
        ? Results.Ok(principal.User)
        : Unauthorized());

app.MapPost("/api/me/password", async (
    HttpContext ctx, ChangePasswordRequest request, IdentityCoordinator identities, AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.IsApiKey) return Forbidden();
    if (MissingFields(request.CurrentPassword, request.NewPassword) is { } bad) return bad;
    try
    {
        await identities.ChangePasswordAsync(principal.User.Id, request, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

// 系统管理员修改自己的显示名；显示名是老师绑定课表科目教师名的依据，其他账号不能自助修改。
app.MapPost("/api/me/display-name", async (
    HttpContext ctx, ChangeDisplayNameRequest request, IdentityCoordinator identities,
    AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.IsApiKey) return Forbidden();
    if (MissingFields(request.DisplayName) is { } bad) return bad;
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    try
    {
        await identities.ChangeDisplayNameAsync(principal.User.Id, request, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

// “我的日程”：当前老师或班主任账号按显示名绑定课表后，跨班级聚合出的个人课表。
app.MapGet("/api/me/schedule", async (
    HttpContext ctx, IdentityCoordinator identities, TeacherBindingService teachers, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    // 只有内置“老师”与“班主任”角色按显示名绑定课表；其他账号即使同名也返回空日程，与 WebUI“我的日程”一致。
    if (!HasPersonalSchedule(principal)) return Results.Ok(new MyScheduleResponse());
    return Results.Ok(await teachers.BuildMyScheduleAsync(principal.User.DisplayName, ct));
});

// “我的日程”的下一节课：返回老师正在上的课和接下来要上的课（含班级与起止时间），
// 供脚本或 Agent 直接回答“下节课去哪个班上什么”。at 省略时取服务端当前时间。
app.MapGet("/api/me/schedule/next", async (
    HttpContext ctx, DateTimeOffset? at, IdentityCoordinator identities, TeacherBindingService teachers, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    var now = at ?? DateTimeOffset.UtcNow;
    if (!HasPersonalSchedule(principal)) return Results.Ok(new MyNextCourseResponse { At = now });
    return Results.Ok(await teachers.BuildMyNextCourseAsync(principal.User.DisplayName, now, ct));
});

// ---------- 换课申请：老师发起、对方老师审批；强制换课立即生效、对方可撤回 ----------

app.MapGet("/api/swap-requests/catalog", async (
    HttpContext ctx, IdentityCoordinator identities, ScheduleSwapService swaps, CancellationToken ct) =>
    await SwapCallAsync(ctx, identities, principal => swaps.GetCatalogAsync(principal.User!.Id, ct), ct));

app.MapGet("/api/swap-requests", async (
    HttpContext ctx, string? box, SwapRequestStatus? status, IdentityCoordinator identities, ScheduleSwapService swaps,
    CancellationToken ct) =>
    await SwapCallAsync(ctx, identities, principal => swaps.ListAsync(principal.User!.Id, box ?? "all", status, ct), ct));

app.MapGet("/api/swap-requests/{id}", async (
    string id, HttpContext ctx, IdentityCoordinator identities, ScheduleSwapService swaps, CancellationToken ct) =>
    await SwapCallAsync(ctx, identities, async principal =>
        await swaps.GetAsync(principal.User!.Id, await ResolveSwapIdAsync(swaps, principal, id, ct), ct), ct));

app.MapPost("/api/swap-requests", async (
    HttpContext ctx, CreateSwapRequest request, IdentityCoordinator identities, ScheduleSwapService swaps, CancellationToken ct) =>
    await SwapCallAsync(ctx, identities, principal => swaps.CreateAsync(principal.User!.Id, request, ct), ct));

app.MapPost("/api/swap-requests/{id}/approve", async (
    string id, HttpContext ctx, SwapDecisionRequest? request, IdentityCoordinator identities, ScheduleSwapService swaps,
    CancellationToken ct) =>
    await SwapCallAsync(ctx, identities, async principal =>
        await swaps.ApproveAsync(principal.User!.Id, await ResolveSwapIdAsync(swaps, principal, id, ct), request?.Note, ct), ct));

app.MapPost("/api/swap-requests/{id}/reject", async (
    string id, HttpContext ctx, SwapDecisionRequest? request, IdentityCoordinator identities, ScheduleSwapService swaps,
    CancellationToken ct) =>
    await SwapCallAsync(ctx, identities, async principal =>
        await swaps.RejectAsync(principal.User!.Id, await ResolveSwapIdAsync(swaps, principal, id, ct), request?.Note, ct), ct));

app.MapPost("/api/swap-requests/{id}/cancel", async (
    string id, HttpContext ctx, IdentityCoordinator identities, ScheduleSwapService swaps, CancellationToken ct) =>
    await SwapCallAsync(ctx, identities, async principal =>
        await swaps.CancelAsync(principal.User!.Id, await ResolveSwapIdAsync(swaps, principal, id, ct), ct), ct));

app.MapPost("/api/swap-requests/{id}/revoke", async (
    string id, HttpContext ctx, IdentityCoordinator identities, ScheduleSwapService swaps, CancellationToken ct) =>
    await SwapCallAsync(ctx, identities, async principal =>
        await swaps.RevokeForcedAsync(principal.User!.Id, await ResolveSwapIdAsync(swaps, principal, id, ct), ct), ct));

// 个人通知：WebUI 铃铛、手机离线补齐与 AstrBot 轮询共用；after 为上次读取到的最新通知时间。
app.MapGet("/api/me/notifications", async (
    HttpContext ctx, DateTimeOffset? after, bool? unread, int? limit, IdentityCoordinator identities,
    UserNotificationService notifications, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    return Results.Ok(await notifications.ListAsync(principal.User.Id, after, unread == true, limit ?? 50, ct));
});

app.MapPost("/api/me/notifications/read", async (
    HttpContext ctx, MarkNotificationsReadRequest request, IdentityCoordinator identities,
    UserNotificationService notifications, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    await notifications.MarkReadAsync(principal.User.Id, request.Ids, request.All, ct);
    return Results.NoContent();
});

app.MapGet("/api/me/sessions", async (HttpContext ctx, IdentityCoordinator identities, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.IsApiKey) return Forbidden();
    return Results.Ok(await identities.ListSessionsAsync(principal.User.Id, principal.DeviceSessionId, ct));
});

app.MapDelete("/api/me/sessions/{id:guid}", async (
    Guid id, HttpContext ctx, IdentityCoordinator identities, AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.IsApiKey) return Forbidden();
    try
    {
        await identities.RevokeSessionAsync(principal.User.Id, id, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

app.MapGet("/api/state", async (
    HttpContext ctx, Guid? classId, IdentityCoordinator identities, ClassAccessService access, IStateStore store, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (await ResolveClassAsync(principal, classId, access, ct) is not { } target) return Forbidden();
    return store.GetLatestSnapshot(target) is { } snapshot
        ? Results.Ok(snapshot)
        : Results.Json(Error(ApiErrorCodes.NotFound, "尚无课程状态"), statusCode: StatusCodes.Status404NotFound);
});

app.MapGet("/api/schedule", async (
    HttpContext ctx, Guid? classId, IdentityCoordinator identities, ClassAccessService access, IStateStore store, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (await ResolveClassAsync(principal, classId, access, ct) is not { } target) return Forbidden();
    return store.GetLatestSchedule(target) is { } schedule
        ? Results.Ok(schedule)
        : Results.Json(Error(ApiErrorCodes.NotFound, "尚无课表"), statusCode: StatusCodes.Status404NotFound);
});

app.MapPost("/api/commands", async (
    HttpContext ctx, CommandMessage command, Guid? classId, IdentityCoordinator identities,
    ClassAccessService access, PeerRegistry peers, IStateStore store, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    // 档案与扩展设置只能经各自的专用入口复核班级身份、自治策略与修订号，不能通过通用命令 API 绕过。
    if (CommandPermissions.IsServerOnly(command.Command))
        return command.Command == CommandKind.ApplyExtensionSettings
            ? Results.BadRequest(Error(ApiErrorCodes.InvalidRequest,
                "请使用 PUT /api/classes/{classId}/extension-groups/{groupId}/settings 修改扩展设置"))
            : Results.Json(Error(ApiErrorCodes.Forbidden,
                "请通过档案管理页面、POST /api/profiles/collect 或 POST /api/profiles/apply 收集与下发档案"), statusCode: StatusCodes.Status403Forbidden);
    if (await ResolveClassAsync(principal, command.ClassId ?? classId, access, ct) is not { } target) return Forbidden();
    command.ClassId = target;
    var classPermissions = await access.GetEffectivePermissionsAsync(
        principal.User.Id, principal.User.Role, target, principal.User.GrantedPermissions, ct);
    if (command.Command == CommandKind.RunExtension)
    {
        // 与 WS 路径一致：独立扩展权限和管理员逐扩展策略必须同时通过。
        if (string.IsNullOrEmpty(command.ExtensionId))
            return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "缺少扩展 Id"));
        var definition = store.GetLatestExtensions(target)?.FirstOrDefault(x => x.Id == command.ExtensionId);
        if (definition is null)
            return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "扩展功能不存在或尚未同步"));
        if (!ExtensionAccess.CanInvoke(classPermissions, principal.User.AllowedExtensionIds, definition)) return Forbidden();
    }
    else
    {
        var required = CommandPermissions.Required(command.Command);
        if (required == UserPermissions.None) return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "未知命令"));
        if (!classPermissions.HasFlag(required)) return Forbidden();
    }
    // 与 WS 路径一致：署名标志由服务端全局设置决定，REST 客户端不能绕过。
    if (command.Notification is not null)
        command.Notification.ForceSenderInTitle = await identities.GetForceSenderInTitleAsync(ct);
    command.RequestedBy = principal.User.WithPermissions(classPermissions);
    var result = await peers.SendCommandAndWaitAsync(command, target, TimeSpan.FromSeconds(15), ct);
    return Results.Json(result, statusCode: CommandStatus(result));
});

var usersApi = app.MapGroup("/api/users");
usersApi.MapGet("/", async (HttpContext ctx, IdentityCoordinator identities, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    return HasPermission(principal, UserPermissions.ManageUsers)
        ? Results.Ok(await identities.ListUsersAsync(ct))
        : Forbidden();
});
usersApi.MapPost("/", async (
    HttpContext ctx, CreateUserRequest request, IdentityCoordinator identities, AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (!HasPermission(principal, UserPermissions.ManageUsers)) return Forbidden();
    if (MissingFields(request.Username, request.Password) is { } bad) return bad;
    // 被授予 ManageUsers 的普通用户只能管理普通账号，不能创建管理员。
    if ((request.Role == UserRole.Admin || request.RoleId == AccountRole.AdministratorId) && principal.User.Role != UserRole.Admin) return Forbidden();
    try
    {
        var created = await identities.CreateUserAsync(request, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.Created($"/api/users/{created.Id}", created);
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
usersApi.MapPut("/{id:guid}", async (
    Guid id, HttpContext ctx, UpdateUserRequest request, IdentityCoordinator identities, AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (!HasPermission(principal, UserPermissions.ManageUsers)) return Forbidden();
    // 不能把账号升级为管理员；管理员账号本身也只有管理员能编辑（含禁用状态的管理员）。
    if (principal.User.Role != UserRole.Admin &&
        ((request.Role == UserRole.Admin || request.RoleId == AccountRole.AdministratorId) ||
         await identities.GetRoleAsync(id, ct) == UserRole.Admin))
        return Forbidden();
    try
    {
        var updated = await identities.UpdateUserAsync(id, request, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.Ok(updated);
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
usersApi.MapPost("/{id:guid}/password", async (
    Guid id, HttpContext ctx, ResetPasswordRequest request, IdentityCoordinator identities, AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (!HasPermission(principal, UserPermissions.ManageUsers)) return Forbidden();
    if (MissingFields(request.Password) is { } bad) return bad;
    // 重置管理员密码等于接管管理员账号，仅管理员可执行（含禁用状态的管理员）。
    if (principal.User.Role != UserRole.Admin &&
        await identities.GetRoleAsync(id, ct) == UserRole.Admin)
        return Forbidden();
    try
    {
        await identities.ResetPasswordAsync(id, request.Password, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
// 批量导入人员：行格式 ID,用户名,班级,角色,密码（密码可空=待激活），逐行回报结果。
usersApi.MapPost("/batch-import", async (
    HttpContext ctx, BatchImportRequest request, IdentityCoordinator identities, UserImportService importService,
    AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    try
    {
        var result = await importService.ImportAsync(request.Text, request.DefaultClassId, request.DefaultRoleId, ct);
        if (result.Created > 0) await authorizationSync.SyncAsync(ct);
        return Results.Ok(result);
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
usersApi.MapDelete("/{id:guid}", async (
    Guid id, HttpContext ctx, IdentityCoordinator identities, AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (!HasPermission(principal, UserPermissions.ManageUsers)) return Forbidden();
    // 删除管理员账号仅管理员可执行（含禁用状态；最后管理员另有 GuardLastAdmin 保护）。
    if (principal.User.Role != UserRole.Admin &&
        await identities.GetRoleAsync(id, ct) == UserRole.Admin)
        return Forbidden();
    try
    {
        await identities.DeleteUserAsync(id, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

app.MapPost("/api/plugin/pairing-code", async (
    HttpContext ctx, PairingCodeBody? body, IdentityCoordinator identities, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (!HasPermission(principal, UserPermissions.ManageUsers)) return Forbidden();
    var classId = body?.ClassId ?? Classroom.DefaultId;
    // unified=统一连接码；persistent=班级固定码；否则为绑定班级的一次性码。
    var pairCode = body?.Unified == true
        ? await identities.CreateSharedPluginPairingCodeAsync(body?.RequestedCode, ct)
        : body?.Persistent == true
            ? await identities.SetClassPairingCodeAsync(classId, body?.RequestedCode, ct)
            : await identities.CreatePluginPairingCodeAsync(classId, ct);
    return Results.Ok(new { pairCode });
});

// 班级分组：管理员组织班级（如按年级），批量操作与广播通知可按组展开。
var groupApi = app.MapGroup("/api/class-groups");
groupApi.MapGet("/", async (
    HttpContext ctx, IdentityCoordinator identities, ClassroomService classrooms, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    return Results.Ok(await classrooms.ListGroupsAsync(ct));
});
groupApi.MapPost("/", async (
    HttpContext ctx, CreateClassGroupRequest request, IdentityCoordinator identities, ClassroomService classrooms, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    if (MissingFields(request.Name) is { } bad) return bad;
    try { return Results.Ok(await classrooms.CreateGroupAsync(request.Name, request.ParentId, ct)); }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
groupApi.MapPut("/{id:guid}", async (
    Guid id, HttpContext ctx, UpdateClassGroupRequest request, IdentityCoordinator identities, ClassroomService classrooms, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    if (MissingFields(request.Name) is { } bad) return bad;
    try { await classrooms.RenameGroupAsync(id, request.Name, request.ParentId, ct); return Results.NoContent(); }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
// 整体替换一个分组包含的班级（仅直接归属）。
groupApi.MapPut("/{id:guid}/classes", async (
    Guid id, HttpContext ctx, UpdateGroupClassesRequest request, IdentityCoordinator identities, ClassroomService classrooms,
    AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    try
    {
        await classrooms.SetGroupClassesAsync(id, request.ClassIds, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
groupApi.MapDelete("/{id:guid}", async (
    Guid id, HttpContext ctx, IdentityCoordinator identities, ClassroomService classrooms, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    try { await classrooms.DeleteGroupAsync(id, ct); return Results.NoContent(); }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

// 集控广播：一次向多个班级/分组发送通知、清除通知、电源或语音消息，逐班按班内权限鉴权并回报结果。
app.MapPost("/api/commands/broadcast", async (
    HttpContext ctx, BroadcastCommandRequest request, IdentityCoordinator identities,
    ClassBroadcastService broadcast, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (request.ClassIds.Count == 0 && (request.GroupIds?.Count ?? 0) == 0)
        return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "请选择要操作的班级或分组"));
    if (request.Command is CommandKind.SendNotification && NotificationRequest.Validate(request.Notification) is { } notificationError)
        return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, notificationError));
    if (request.Command is CommandKind.SendVoiceMessage &&
        !VoiceMessageRequest.TryDecode(request.VoiceMessage, out _))
        return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "语音格式无效或超过 60 秒"));
    try
    {
        return Results.Ok(await broadcast.BroadcastAsync(principal, request, ct));
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

// 当前账号可访问的班级：客户端班级选择/切换的数据源。
app.MapGet("/api/me/classes", async (
    HttpContext ctx, IdentityCoordinator identities, ClassAccessService access, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    return Results.Ok(await access.GetAccessibleClassesAsync(principal.User.Id, principal.User.Role, principal.User.GrantedPermissions, ct));
});

// 班级管理：仅系统管理员。
var classesApi = app.MapGroup("/api/classes");
classesApi.MapGet("/", async (
    HttpContext ctx, IdentityCoordinator identities, ClassroomService classrooms, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    return Results.Ok(await classrooms.ListAsync(ct));
});
classesApi.MapPost("/", async (
    HttpContext ctx, CreateClassRequest request, IdentityCoordinator identities, ClassroomService classrooms,
    AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    if (MissingFields(request.Name) is { } bad) return bad;
    try
    {
        var created = await classrooms.CreateAsync(request.Name, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.Created($"/api/classes/{created.Id}", created);
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
classesApi.MapPut("/{id:guid}", async (
    Guid id, HttpContext ctx, UpdateClassRequest request, IdentityCoordinator identities, ClassroomService classrooms, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    if (MissingFields(request.Name) is { } bad) return bad;
    try { await classrooms.RenameAsync(id, request.Name, ct); return Results.NoContent(); }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
classesApi.MapDelete("/{id:guid}", async (
    Guid id, HttpContext ctx, IdentityCoordinator identities, ClassroomService classrooms,
    AuthorizationSyncService authorizationSync, PeerRegistry peers, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    try
    {
        await classrooms.DeleteAsync(id, ct);
        // 班级删除会级联清理插件凭据；对应在线连接立即断开，重新配对前不再接收命令。
        await peers.DisconnectPluginClassAsync(id, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
// 班级信息：系统管理员或本班班主任可改班名；头像小图直存数据库。
classesApi.MapPut("/{id:guid}/info", async (
    Guid id, HttpContext ctx, UpdateClassRequest request, IdentityCoordinator identities, ClassAccessService access,
    ClassroomService classrooms, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (!(await access.GetClassSelfServiceAsync(principal.User.Id, principal.User.Role, id, ct)).CanRename) return Forbidden();
    if (MissingFields(request.Name) is { } bad) return bad;
    try
    {
        await classrooms.RenameClassAsync(id, request.Name, ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
// 头像走原始字节上传（Bearer 客户端没有 antiforgery 令牌，不用 multipart 表单）。
classesApi.MapPut("/{id:guid}/avatar", async (
    Guid id, HttpContext ctx, IdentityCoordinator identities, ClassAccessService access,
    ClassroomService classrooms, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (!(await access.GetClassSelfServiceAsync(principal.User.Id, principal.User.Role, id, ct)).CanChangeAvatar) return Forbidden();
    var contentType = ctx.Request.Headers["X-Avatar-Type"].ToString();
    var allowed = new[] { "image/png", "image/jpeg", "image/webp" };
    if (!allowed.Contains(contentType))
        return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "头像仅支持 PNG/JPEG/WebP"));
    if (ctx.Request.ContentLength is null or 0 or > 256 * 1024)
        return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "头像需为不超过 256KB 的图片"));
    using var memory = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(memory, ct);
    await classrooms.SetAvatarAsync(id, memory.ToArray(), contentType, ct);
    return Results.NoContent();
});
classesApi.MapDelete("/{id:guid}/avatar", async (
    Guid id, HttpContext ctx, IdentityCoordinator identities, ClassAccessService access,
    ClassroomService classrooms, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (!(await access.GetClassSelfServiceAsync(principal.User.Id, principal.User.Role, id, ct)).CanChangeAvatar) return Forbidden();
    await classrooms.SetAvatarAsync(id, null, null, ct);
    return Results.NoContent();
});
// 头像属于班级公开信息（访客页也展示），无需鉴权；ETag 支持客户端缓存。
app.MapGet("/api/classes/{id:guid}/avatar", async (
    Guid id, HttpContext ctx, ClassroomService classrooms, CancellationToken ct) =>
{
    var classroom = await classrooms.RequireAsync(id, ct);
    if (classroom.Avatar is null) return Results.NotFound();
    var etag = $"\"{classroom.AvatarUpdatedAt?.Ticks ?? 0:x}\"";
    if (ctx.Request.Headers.IfNoneMatch == etag)
        return Results.StatusCode(StatusCodes.Status304NotModified);
    ctx.Response.Headers.ETag = etag;
    return Results.File(classroom.Avatar, classroom.AvatarContentType ?? "image/png");
});

classesApi.MapPut("/{id:guid}/visitor", async (
    Guid id, HttpContext ctx, ClassVisitorRequest request, IdentityCoordinator identities, ClassroomService classrooms, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    try
    {
        await classrooms.SetVisitorAccessAsync(id, request.Enabled, ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
// 整体替换一个班级所属的分组（多归属）。
classesApi.MapPut("/{id:guid}/groups", async (
    Guid id, HttpContext ctx, UpdateClassGroupsRequest request, IdentityCoordinator identities, ClassroomService classrooms, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    try
    {
        await classrooms.SetClassGroupsAsync(id, request.GroupIds, ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
classesApi.MapGet("/{id:guid}/members", async (
    Guid id, HttpContext ctx, IdentityCoordinator identities, ClassroomService classrooms, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    try { return Results.Ok(await classrooms.ListMembersAsync(id, ct)); }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
classesApi.MapPut("/{id:guid}/members", async (
    Guid id, HttpContext ctx, UpdateClassMembersRequest request, IdentityCoordinator identities, ClassroomService classrooms,
    AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    try
    {
        await classrooms.UpdateMembersAsync(id, request, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
classesApi.MapPost("/batch", async (
    HttpContext ctx, BatchClassOperationRequest request, IdentityCoordinator identities, ClassroomService classrooms,
    AuthorizationSyncService authorizationSync, PeerRegistry peers, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    if (MissingFields(request.Operation) is { } bad) return bad;
    BatchClassOperationResult result;
    try
    {
        result = await classrooms.BatchAsync(request, ct);
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
    if (request.Operation.Trim().Equals("delete", StringComparison.OrdinalIgnoreCase) ||
        request.Operation.Trim().Equals("enablevisitor", StringComparison.OrdinalIgnoreCase) ||
        request.Operation.Trim().Equals("disablevisitor", StringComparison.OrdinalIgnoreCase))
    {
        if (result.Results.Any(x => x.Success))
        {
            if (request.Operation.Trim().Equals("delete", StringComparison.OrdinalIgnoreCase))
                await peers.DisconnectPluginClassAsync([.. request.ClassIds.Distinct()], ct);
            await authorizationSync.SyncAsync(ct);
        }
    }
    return Results.Ok(result);
});

// 插件长期凭据管理：仅管理员可列举与吊销；吊销后通过连接注册表立即断开对应插件。
app.MapGet("/api/plugins/credentials", async (
    HttpContext ctx, IdentityCoordinator identities, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    return Results.Ok(await identities.ListPluginCredentialsAsync(ct));
});

app.MapDelete("/api/plugins/credentials/{id:guid}", async (
    Guid id, HttpContext ctx, IdentityCoordinator identities, PeerRegistry peers, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    try
    {
        await identities.RevokePluginCredentialAsync(id, ct);
        await peers.DisconnectPluginCredentialAsync(id, ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

app.MapGet("/api/admin/status", async (
    HttpContext ctx, IdentityCoordinator identities, PeerRegistry peers, ClassroomService classrooms, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (!HasPermission(principal, UserPermissions.AccessWebUi)) return Forbidden();
    var classes = await classrooms.ListAsync(ct);
    return Results.Ok(new
    {
        pluginOnline = peers.HasPlugin,
        pluginConnections = peers.PluginCount,
        watchConnections = peers.WatchCount,
        mobileConnections = peers.MobileCount,
        accountCount = (await identities.ListUsersAsync(ct)).Count,
        classes = classes.Select(x => new
        {
            id = x.Id,
            name = x.Name,
            pluginOnline = x.PluginCount > 0,
        }),
        protocolVersion = Protocol.Version,
    });
});

app.MapGet("/api/roles", async (HttpContext ctx, IdentityCoordinator identities, AccountRoleService roles, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (!HasPermission(principal, UserPermissions.ManageUsers)) return Forbidden();
    return Results.Ok(await roles.ListAsync(ct));
});
app.MapPost("/api/roles", async (HttpContext ctx, CreateAccountRoleRequest request, AccountRoleService roles, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities: ctx.RequestServices.GetRequiredService<IdentityCoordinator>(), ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    if (MissingFields(request.Name) is { } bad) return bad;
    try { return Results.Ok(await roles.CreateAsync(request.Name, request.DefaultPermissions, ct)); }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
app.MapPut("/api/roles/{id:guid}", async (Guid id, HttpContext ctx, UpdateAccountRoleRequest request, AccountRoleService roles, AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, ctx.RequestServices.GetRequiredService<IdentityCoordinator>(), ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    try
    {
        await roles.UpdateAsync(id, request.Name, request.DefaultPermissions, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
app.MapDelete("/api/roles/{id:guid}", async (Guid id, HttpContext ctx, AccountRoleService roles, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, ctx.RequestServices.GetRequiredService<IdentityCoordinator>(), ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    try { await roles.DeleteAsync(id, ct); return Results.NoContent(); }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});
// 访客设置：autoEnter 是全局登录页行为；“哪些班级开放访客”在班级管理中逐班配置。
app.MapGet("/api/visitor", async (
    HttpContext ctx, IdentityCoordinator identities, VisitorAccessSettings visitor, ClassroomService classrooms, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (!HasPermission(principal, UserPermissions.ManageUsers)) return Forbidden();
    var classes = await classrooms.ListAsync(ct);
    return Results.Ok(new
    {
        autoEnter = await visitor.GetAutoEnterAsync(ct),
        anyVisitorClass = classes.Any(x => x.VisitorEnabled),
        classes = classes.Select(x => new { id = x.Id, name = x.Name, visitorEnabled = x.VisitorEnabled }),
    });
});
app.MapPut("/api/visitor", async (
    VisitorAutoEnterBody body, HttpContext ctx, IdentityCoordinator identities, VisitorAccessSettings visitor, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (!HasPermission(principal, UserPermissions.ManageUsers)) return Forbidden();
    return Results.Ok(new { autoEnter = await visitor.SetAutoEnterAsync(body.AutoEnter, ct) });
});
app.MapGet("/api/settings/notifications", async (HttpContext ctx, IdentityCoordinator identities, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    return Results.Ok(new SettingsSync { ForceSenderInTitle = await identities.GetForceSenderInTitleAsync(ct) });
});
app.MapPut("/api/settings/notifications", async (SettingsSync body, HttpContext ctx, IdentityCoordinator identities, PeerRegistry peers, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin)
        return Forbidden();
    var updated = await identities.SetForceSenderInTitleAsync(body.ForceSenderInTitle, ct);
    await peers.SendSettingsToWatchesAsync(updated, ct);
    return Results.Ok(updated);
});
// 班级自治策略：系统管理员统一决定班主任能否自行改班名、改头像、拉取课表与修改扩展设置；任何登录账号都可读取。
app.MapGet("/api/settings/class-self-service", async (HttpContext ctx, IdentityCoordinator identities, ClassSelfServiceSettings selfService, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    return Results.Ok(await selfService.GetAsync(ct));
});
app.MapPut("/api/settings/class-self-service", async (HttpContext ctx, ClassSelfServicePolicy body, IdentityCoordinator identities, ClassSelfServiceSettings selfService, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    await selfService.SetAsync(body, ct);
    return Results.Ok(await selfService.GetAsync(ct));
});
app.MapGet("/api/settings/schedule-pull", async (HttpContext ctx, IdentityCoordinator identities, SchedulePullSettings pull, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    return Results.Ok(new { intervalMinutes = (int)await pull.GetIntervalAsync(ct) });
});
app.MapPut("/api/settings/schedule-pull", async (HttpContext ctx, SchedulePullIntervalBody body, IdentityCoordinator identities, ClassAccessService access, SchedulePullSettings pull, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    // 定时拉取间隔对全部班级生效，属于全局设置，只允许系统管理员修改。
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    var interval = Enum.IsDefined(typeof(SchedulePullInterval), body.IntervalMinutes)
        ? (SchedulePullInterval)body.IntervalMinutes
        : SchedulePullInterval.Disabled;
    await pull.SetIntervalAsync(interval, ct);
    return Results.Ok(new { intervalMinutes = (int)interval });
});
// 登录页背景图属于未登录可见的公开资源，无需鉴权；ETag 让浏览器在图片未变时复用缓存。
app.MapGet("/api/settings/login-background", async (HttpContext ctx, LoginPageSettings loginPage, CancellationToken ct) =>
{
    var metadata = await loginPage.GetAsync(ct);
    if (metadata.LoginBackground is null) return Results.NotFound();
    var etag = $"\"{metadata.LoginBackgroundUpdatedAt?.Ticks ?? 0:x}\"";
    if (ctx.Request.Headers.IfNoneMatch == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
    ctx.Response.Headers.ETag = etag;
    return Results.File(metadata.LoginBackground, metadata.LoginBackgroundContentType ?? "image/png");
});app.MapGet("/api/extensions", async (
    HttpContext ctx, Guid? classId, IdentityCoordinator identities, ClassAccessService access,
    ExtensionPolicyService policies, IStateStore store, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (await ResolveClassAsync(principal, classId, access, ct) is not { } target) return Forbidden();
    var definitions = store.GetLatestExtensions(target) ?? [];
    var classPermissions = await access.GetEffectivePermissionsAsync(principal.User.Id, principal.User.Role, target, principal.User.GrantedPermissions, ct);
    var items = await policies.ListForUserAsync(principal.User.Id, principal.User.Role, classPermissions, definitions, ct);
    return Results.Ok(items.Select(item => new
    {
        id = item.Definition.Id,
        displayName = item.Definition.DisplayName,
        enabled = item.Enabled,
        allowNonAdmin = item.AllowNonAdmin,
        showOnWatch = item.ShowOnWatch,
        canInvoke = item.CanInvoke,
        classId = target,
    }));
});
app.MapPut("/api/extensions/{id}", async (string id, ExtensionPolicyBody body, HttpContext ctx, IdentityCoordinator identities, ExtensionPolicyService policies, AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    try
    {
        if (principal.User.Role == UserRole.Admin)
            await policies.UpdateAdminAsync(principal.User.Id, id, body.Enabled ?? true, body.AllowNonAdmin ?? false, body.ShowOnWatch ?? true, ct);
        else
            await policies.UpdatePersonalAsync(principal.User.Id, id, body.ShowOnWatch ?? true, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.NoContent();
    }
    catch (Exception ex) { return Results.Json(Error(ApiErrorCodes.InvalidRequest, ex.Message), statusCode: 400); }
});
// 扩展分组与设置：分组定义与当前值来自插件上报；只有可修改设置的账号才能看到当前值。
app.MapGet("/api/extension-groups", async (
    HttpContext ctx, Guid? classId, IdentityCoordinator identities, ClassAccessService access,
    ExtensionGroupService groups, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (await ResolveClassAsync(principal, classId, access, ct) is not { } target) return Forbidden();
    var classPermissions = await access.GetEffectivePermissionsAsync(
        principal.User.Id, principal.User.Role, target, principal.User.GrantedPermissions, ct);
    if (!classPermissions.HasFlag(UserPermissions.RunExtensions)) return Forbidden();
    var canEdit = await groups.CanEditSettingsAsync(principal.User, target, ct);
    return Results.Ok(groups.BuildForClass(target)
        .Where(group => !group.IsUngrouped)
        .Select(group => new
        {
            id = group.Id,
            displayName = group.DisplayName,
            description = group.Description,
            icon = group.Icon,
            settings = group.Settings,
            values = canEdit ? group.Values : null,
            canEditSettings = canEdit && group.HasSettings,
            classId = target,
        }));
});
app.MapPut("/api/classes/{classId:guid}/extension-groups/{groupId}/settings", async (
    Guid classId, string groupId, ExtensionSettingsBody body, HttpContext ctx, IdentityCoordinator identities,
    ClassAccessService access, ExtensionGroupService groups, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (await ResolveClassAsync(principal, classId, access, ct) is not { } target) return Forbidden();
    if (!await groups.CanEditSettingsAsync(principal.User, target, ct)) return Forbidden();
    var result = await groups.ApplyToClassAsync(
        principal.User, target, groupId, body.Values ?? new Dictionary<string, string?>(), ct);
    return Results.Json(result, statusCode: CommandStatus(result));
});
app.MapGet("/api/admin/system", async (HttpContext ctx, IdentityCoordinator identities, UpdateService updates, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    var development = ctx.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment();
    return Results.Ok(new
    {
        currentVersion = updates.CurrentVersion,
        canSelfUpdate = UpdateService.CanSelfUpdateNow(development),
        message = UpdateService.CurrentApplyMode == UpdateApplyMode.ManagedByPlatform ? UpdateService.FnosManagedMessage : "",
    });
});
app.MapPost("/api/admin/updates/check", async (UpdateCheckBody body, HttpContext ctx, IdentityCoordinator identities, UpdateService updates, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    var channel = string.Equals(body.Channel, "beta", StringComparison.OrdinalIgnoreCase) ? UpdateChannel.Beta : UpdateChannel.Stable;
    var release = await updates.FetchLatestReleaseAsync(channel, ct);
    return Results.Ok(new { tag = release?.Tag, name = release?.Name });
});
app.MapGet("/api/admin/backups", async (HttpContext ctx, IdentityCoordinator identities, ConfigurationArchiveService archives, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    return Results.Ok(archives.ListBackups());
});
app.MapPost("/api/admin/backups", async (HttpContext ctx, IdentityCoordinator identities, ConfigurationArchiveService archives, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    await archives.CreateLocalBackupAsync("manual", ct);
    return Results.NoContent();
});
app.MapDelete("/api/admin/backups/{name}", async (string name, HttpContext ctx, IdentityCoordinator identities, ConfigurationArchiveService archives, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    try { archives.DeleteBackup(name); return Results.NoContent(); }
    catch { return Results.Json(Error(ApiErrorCodes.NotFound, "备份不存在"), statusCode: 404); }
});
app.MapPost("/api/admin/backups/{name}/restore", async (string name, HttpContext ctx, IdentityCoordinator identities, ConfigurationArchiveService archives, PeerRegistry peers, IHostApplicationLifetime lifetime, IHostEnvironment environment, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (principal.User.Role != UserRole.Admin) return Forbidden();
    await archives.CreateLocalBackupAsync("preimport", ct);
    await archives.ApplyAsync(archives.ParseLocalBackup(archives.ReadBackup(name)), ct);
    await peers.DisconnectAllAsync(ct);
    ApplicationRestartCoordinator.ScheduleRestart(lifetime, environment);
    return Results.NoContent();
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", protocolVersion = Protocol.Version }));
app.MapRazorPages();
app.MapHolidayEndpoints();
app.MapProfileEndpoints();
app.Run();

static async Task<AuthPrincipal?> AuthorizeAsync(HttpContext ctx, IdentityCoordinator identities, CancellationToken ct)
{
    // 现有客户端统一使用 Bearer；API Key 也复用该标准头，服务端按固定前缀分流，
    // 同时兼容部分脚本客户端习惯使用的 X-API-Key。
    var header = ctx.Request.Headers.Authorization.ToString();
    var token = header.StartsWith($"{Protocol.BearerScheme} ", StringComparison.OrdinalIgnoreCase)
        ? header[(Protocol.BearerScheme.Length + 1)..].Trim()
        : ctx.Request.Headers["X-API-Key"].ToString().Trim();
    if (string.IsNullOrWhiteSpace(token)) return null;
    return token.StartsWith(IdentityCoordinator.ApiKeyPrefix, StringComparison.Ordinal)
        ? await identities.ValidateApiKeyAsync(token, ct)
        : await identities.ValidateAccessTokenAsync(token, ct);
}

/// <summary>换课端点统一的鉴权与错误映射：SwapOperationException 转为带错误码的 JSON。</summary>
static async Task<IResult> SwapCallAsync<T>(
    HttpContext ctx, IdentityCoordinator identities, Func<AuthPrincipal, Task<T>> action, CancellationToken ct)
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    try { return Results.Ok(await action(principal)); }
    catch (SwapOperationException ex) { return Results.Json(Error(ex.Code, ex.Message), statusCode: ex.Status); }
}

/// <summary>换课申请编号既接受完整 Id，也接受聊天机器人里展示的 8 位短编号。</summary>
static async Task<Guid> ResolveSwapIdAsync(ScheduleSwapService swaps, AuthPrincipal principal, string id, CancellationToken ct) =>
    await swaps.ResolveShortIdAsync(principal.User!.Id, id, ct)
    ?? throw new SwapOperationException(ApiErrorCodes.NotFound, "换课申请不存在", StatusCodes.Status404NotFound);

static bool HasPermission(AuthPrincipal? principal, UserPermissions permission) =>
    principal?.User?.Permissions.HasFlag(permission) == true;

/// <summary>主体的全局角色是否拥有个人“我的日程”（内置老师或班主任）；按角色种类判断，不受角色改名影响。</summary>
static bool HasPersonalSchedule(AuthPrincipal principal) =>
    principal.User?.RoleKind is { } kind && AccountRole.HasPersonalSchedule((AccountRoleKind)kind);

/// <summary>
/// 解析请求的目标班级：显式 classId 必须可访问（否则 null→403），缺省落到默认班级或第一个成员班级。
/// </summary>
static async Task<Guid?> ResolveClassAsync(
    AuthPrincipal principal, Guid? requested, ClassAccessService access, CancellationToken ct)
{
    var user = principal.User!;
    if (requested is { } id)
        return await access.CanAccessAsync(user.Id, user.Role, id, ct) ? id : null;
    return await access.ResolveDefaultClassIdAsync(user.Id, user.Role, ct);
}

/// <summary>认证端点的必填字段缺失（含 JSON 显式传 null）时返回 400 而不是内部 500。</summary>
static IResult? MissingFields(params string?[] values) =>
    values.Any(string.IsNullOrEmpty)
        ? Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "缺少必填字段"))
        : null;

static int CommandStatus(CommandResult result) => result.Code switch
{
    CommandResultCodes.PluginOffline => StatusCodes.Status503ServiceUnavailable,
    CommandResultCodes.Timeout => StatusCodes.Status504GatewayTimeout,
    CommandResultCodes.Forbidden => StatusCodes.Status403Forbidden,
    CommandResultCodes.InvalidRequest => StatusCodes.Status400BadRequest,
    CommandResultCodes.ScheduleStale => StatusCodes.Status409Conflict,
    CommandResultCodes.Queued => StatusCodes.Status202Accepted,
    _ => result.Success ? StatusCodes.Status200OK : StatusCodes.Status422UnprocessableEntity,
};

static IResult Unauthorized() => Results.Json(
    Error(ApiErrorCodes.Unauthorized, "未登录或登录已失效"), statusCode: StatusCodes.Status401Unauthorized);
static IResult Forbidden() => Results.Json(
    Error(ApiErrorCodes.Forbidden, "权限不足"), statusCode: StatusCodes.Status403Forbidden);
static IResult OperationError(IdentityOperationException ex) => Results.Json(
    Error(ex.Code, ex.Message), statusCode: ex.Code switch
    {
        ApiErrorCodes.Unauthorized => StatusCodes.Status401Unauthorized,
        ApiErrorCodes.Forbidden => StatusCodes.Status403Forbidden,
        ApiErrorCodes.NotFound => StatusCodes.Status404NotFound,
        ApiErrorCodes.PairCodeInvalid => StatusCodes.Status409Conflict,
        ApiErrorCodes.UsernameExists or ApiErrorCodes.LastAdmin => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest,
    });
static ApiError Error(string code, string message) => new() { Code = code, Message = message };

public sealed record SchedulePullIntervalBody(int IntervalMinutes);
public sealed record ExtensionPolicyBody(bool? Enabled, bool? AllowNonAdmin, bool? ShowOnWatch);
public sealed record ExtensionSettingsBody(Dictionary<string, string?>? Values);
public sealed record UpdateCheckBody(string Channel, bool Force = false);
public sealed record PairingCodeBody(Guid? ClassId, bool Unified = false, bool Persistent = false, string? RequestedCode = null);
public sealed record VisitorAutoEnterBody(bool AutoEnter);

public partial class Program;
