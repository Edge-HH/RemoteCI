using Microsoft.EntityFrameworkCore;
using RemoteCI.Server.Data;

namespace RemoteCI.Server.Services;

/// <summary>
/// 登录页外观设置：主题策略、背景图、背景不透明度与卡片位置，仅由系统管理员维护。
/// 图片直接存库（与班级头像一致），随数据库一起备份和迁移。
/// </summary>
public sealed class LoginPageSettings(AppDbContext db)
{
    /// <summary>背景图大小上限；登录页需要比班头像更宽的画布，放宽到 2MB。</summary>
    public const int MaxBackgroundBytes = 2 * 1024 * 1024;

    public static readonly string[] AllowedBackgroundContentTypes = ["image/png", "image/jpeg", "image/webp"];

    /// <summary>读取当前登录页外观设置（含背景图字节，仅供下载背景图使用）。</summary>
    public async Task<SystemMetadata> GetAsync(CancellationToken ct = default) =>
        await db.SystemMetadata.AsNoTracking().SingleAsync(row => row.Id == 1, ct);

    /// <summary>
    /// 只投影渲染所需的字段：未登录页面每次请求都会走这里，不能把整个背景图 blob 读进内存。
    /// </summary>
    public async Task<LoginPageAppearance> GetAppearanceAsync(CancellationToken ct = default) =>
        await db.SystemMetadata.AsNoTracking()
            .Where(row => row.Id == 1)
            .Select(row => new LoginPageAppearance(
                row.LoginTheme,
                row.LoginBackgroundOpacity,
                row.LoginCardPosition,
                row.LoginBackground != null,
                row.LoginBackgroundUpdatedAt))
            .SingleAsync(ct);

    /// <summary>
    /// 更新主题、背景不透明度与卡片位置：不透明度按 0-100 收敛，
    /// 主题越界时回退为跟随偏好，位置越界时回退为居中。
    /// </summary>
    public async Task SetAppearanceAsync(LoginTheme theme, int opacity, LoginCardPosition position, CancellationToken ct = default)
    {
        var metadata = await db.SystemMetadata.SingleAsync(row => row.Id == 1, ct);
        metadata.LoginTheme = Enum.IsDefined(theme) ? theme : LoginTheme.Follow;
        metadata.LoginBackgroundOpacity = Math.Clamp(opacity, 0, 100);
        metadata.LoginCardPosition = Enum.IsDefined(position) ? position : LoginCardPosition.Center;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>设置或清除登录页背景图；image 为 null 表示清除并恢复默认网格背景。</summary>
    public async Task SetBackgroundAsync(byte[]? image, string? contentType, CancellationToken ct = default)
    {
        var metadata = await db.SystemMetadata.SingleAsync(row => row.Id == 1, ct);
        metadata.LoginBackground = image;
        metadata.LoginBackgroundContentType = image is null ? null : contentType;
        metadata.LoginBackgroundUpdatedAt = image is null ? null : DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>渲染登录页所需的登录页外观快照。</summary>
public sealed record LoginPageAppearance(
    LoginTheme Theme,
    int Opacity,
    LoginCardPosition CardPosition,
    bool HasBackground,
    DateTimeOffset? BackgroundUpdatedAt);