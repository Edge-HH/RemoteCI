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
});
builder.Services.AddScoped<IdentityCoordinator>();
builder.Services.AddScoped<AccountRoleService>();
builder.Services.AddScoped<ExtensionPolicyService>();
builder.Services.AddScoped<AuthorizationSyncService>();
builder.Services.AddScoped<ConfigurationArchiveService>();
builder.Services.AddScoped<SchedulePullSettings>();
builder.Services.AddScoped<VisitorAccessSettings>();
builder.Services.AddSingleton<IStateStore, StateStore>();
builder.Services.AddSingleton<PeerRegistry>();
builder.Services.AddSingleton<ScheduleSyncTaskTracker>();
builder.Services.AddSingleton<ScheduleSyncService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHostedService<SchedulePullWorker>();
builder.Services.AddHostedService<PeerAuthorizationRefreshWorker>();
builder.Services.AddHostedService<AutomaticBackupWorker>();
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
    await scope.ServiceProvider.GetRequiredService<IdentityCoordinator>().BootstrapAsync();

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
    if (MissingFields(request.Username, request.Password) is { } bad) return bad;
    try
    {
        var response = await identities.LoginAsync(request, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.Ok(response);
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
    if (MissingFields(request.CurrentPassword, request.NewPassword) is { } bad) return bad;
    try
    {
        await identities.ChangePasswordAsync(principal.User.Id, request, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

app.MapGet("/api/me/sessions", async (HttpContext ctx, IdentityCoordinator identities, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    return principal?.User is null
        ? Unauthorized()
        : Results.Ok(await identities.ListSessionsAsync(principal.User.Id, principal.DeviceSessionId, ct));
});

app.MapDelete("/api/me/sessions/{id:guid}", async (
    Guid id, HttpContext ctx, IdentityCoordinator identities, AuthorizationSyncService authorizationSync, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    try
    {
        await identities.RevokeSessionAsync(principal.User.Id, id, ct);
        await authorizationSync.SyncAsync(ct);
        return Results.NoContent();
    }
    catch (IdentityOperationException ex) { return OperationError(ex); }
});

app.MapGet("/api/state", async (HttpContext ctx, IdentityCoordinator identities, IStateStore store, CancellationToken ct) =>
{
    if (await AuthorizeAsync(ctx, identities, ct) is null) return Unauthorized();
    return store.GetLatestSnapshot() is { } snapshot
        ? Results.Ok(snapshot)
        : Results.Json(Error(ApiErrorCodes.NotFound, "尚无课程状态"), statusCode: StatusCodes.Status404NotFound);
});

app.MapGet("/api/schedule", async (HttpContext ctx, IdentityCoordinator identities, IStateStore store, CancellationToken ct) =>
{
    if (await AuthorizeAsync(ctx, identities, ct) is null) return Unauthorized();
    return store.GetLatestSchedule() is { } schedule
        ? Results.Ok(schedule)
        : Results.Json(Error(ApiErrorCodes.NotFound, "尚无课表"), statusCode: StatusCodes.Status404NotFound);
});

app.MapPost("/api/commands", async (
    HttpContext ctx, CommandMessage command, IdentityCoordinator identities, PeerRegistry peers, IStateStore store, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (command.Command == CommandKind.RunExtension)
    {
        // 与 WS 路径一致：独立扩展权限和管理员逐扩展策略必须同时通过。
        if (string.IsNullOrEmpty(command.ExtensionId))
            return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "缺少扩展 Id"));
        var definition = store.GetLatestExtensions()?.FirstOrDefault(x => x.Id == command.ExtensionId);
        if (definition is null)
            return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "扩展功能不存在或尚未同步"));
        if (!ExtensionAccess.CanInvoke(principal.User, definition)) return Forbidden();
    }
    else
    {
        var required = CommandPermissions.Required(command.Command);
        if (required == UserPermissions.None) return Results.BadRequest(Error(ApiErrorCodes.InvalidRequest, "未知命令"));
        if (!principal.User.Permissions.HasFlag(required)) return Forbidden();
    }
    command.RequestedBy = principal.User;
    var result = await peers.SendCommandAndWaitAsync(command, TimeSpan.FromSeconds(15), ct);
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
    HttpContext ctx, IdentityCoordinator identities, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    return HasPermission(principal, UserPermissions.ManageUsers)
        ? Results.Ok(new { pairCode = await identities.CreatePluginPairingCodeAsync(ct) })
        : Forbidden();
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
    HttpContext ctx, IdentityCoordinator identities, PeerRegistry peers, IStateStore store, CancellationToken ct) =>
{
    var principal = await AuthorizeAsync(ctx, identities, ct);
    if (principal?.User is null) return Unauthorized();
    if (!HasPermission(principal, UserPermissions.AccessWebUi)) return Forbidden();
    return Results.Ok(new
    {
        pluginOnline = peers.HasPlugin,
        pluginConnections = peers.PluginCount,
        watchConnections = peers.WatchCount,
        accountCount = (await identities.ListUsersAsync(ct)).Count,
        latestStateAt = store.GetLatestSnapshot()?.GeneratedAt,
        latestScheduleAt = store.GetLatestSchedule()?.GeneratedAt,
        protocolVersion = Protocol.Version,
    });
});

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", protocolVersion = Protocol.Version }));
app.MapRazorPages();
app.Run();

static async Task<AuthPrincipal?> AuthorizeAsync(HttpContext ctx, IdentityCoordinator identities, CancellationToken ct)
{
    var header = ctx.Request.Headers.Authorization.ToString();
    if (!header.StartsWith($"{Protocol.BearerScheme} ", StringComparison.OrdinalIgnoreCase)) return null;
    return await identities.ValidateAccessTokenAsync(header[(Protocol.BearerScheme.Length + 1)..].Trim(), ct);
}

static bool HasPermission(AuthPrincipal? principal, UserPermissions permission) =>
    principal?.User?.Permissions.HasFlag(permission) == true;

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

public partial class Program;
