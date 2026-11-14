using System.Net;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 从 GitHub Release 取得最新的 ExamAware 安装包和考试插件。
/// 服务端先下载并放入文件中转，再由同一次安装分发给所选班级。
/// </summary>
public sealed class ExamReleaseService(
    FileTransferService transfers,
    ExamRegistry registry,
    ILogger<ExamReleaseService> logger)
{
    public const string AppRepository = "ExamAware/ExamAware2";
    public const string PluginRepository = "Edge-HH/ExamAware-ClassHubConnctor";

    private static readonly HttpClient Download = CreateDownloadClient();
    private static readonly HttpClient Latest = CreateLatestClient();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (ExamGitHubRelease Release, DateTimeOffset ExpiresAt)> _cached = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ExamReleasePreview> PreviewAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var app = await FetchLatestAsync(AppRepository, timeout.Token);
            var plugin = await FetchLatestAsync(PluginRepository, timeout.Token);
            return new ExamReleasePreview(
                Describe(app, ExamReleaseAssets.SelectWindowsInstaller(app?.Assets ?? [])),
                Describe(plugin, ExamReleaseAssets.SelectPluginPackage(plugin?.Assets ?? [])),
                null);
        }
        catch (Exception ex) when (ex is HttpRequestException or FileTransferException or TaskCanceledException)
        {
            logger.LogWarning(ex, "查询 ExamAware Release 失败");
            return new ExamReleasePreview(null, null, ex is FileTransferException transfer ? transfer.Message : "暂时无法查询 GitHub Release，安装时会再试一次。");
        }
    }

    /// <summary>下载最新安装包和插件（已缓存同一版本则直接复用），并登记为即将分发的文件。</summary>
    public async Task PrepareAsync(UserProfile owner, bool includePlugin, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var app = await FetchLatestAsync(AppRepository, ct)
                ?? throw new FileTransferException(ApiErrorCodes.NotFound, $"没有找到 {AppRepository} 的 Release。");
            var installer = ExamReleaseAssets.SelectWindowsInstaller(app.Assets)
                ?? throw new FileTransferException(ApiErrorCodes.NotFound, $"{app.Tag} 里没有 Windows 安装包（*-setup.exe）。");
            await CacheAsync("installer", AppRepository, app.Tag, installer, owner, ct);

            if (!includePlugin) return;
            var plugin = await FetchLatestAsync(PluginRepository, ct)
                ?? throw new FileTransferException(ApiErrorCodes.NotFound,
                    $"考试插件还没有 Release。请先在 {PluginRepository} 发布带 .ea2x 的版本。");
            var package = ExamReleaseAssets.SelectPluginPackage(plugin.Assets)
                ?? throw new FileTransferException(ApiErrorCodes.NotFound, $"{plugin.Tag} 里没有 .ea2x 插件包。");
            await CacheAsync("plugin", PluginRepository, plugin.Tag, package, owner, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task CacheAsync(
        string kind, string repository, string tag, ExamReleaseFile file, UserProfile owner, CancellationToken ct)
    {
        var sourceKey = $"{repository}@{tag}/{file.Name}";
        var staged = registry.Staged(kind);
        if (staged?.SourceKey == sourceKey && transfers.Get(staged.TransferId) is not null)
        {
            logger.LogInformation("复用已下载的 {Kind}：{Source}", kind, sourceKey);
            return;
        }
        if (file.Size > transfers.MaxFileBytes)
            throw new FileTransferException(ApiErrorCodes.InvalidRequest,
                $"{file.Name} 有 {FileTransferService.FormatSize(file.Size)}，超过文件中转上限。");

        var temp = Path.Combine(transfers.Directory, Guid.NewGuid().ToString("N") + ".part");
        try
        {
            logger.LogInformation("正在从 GitHub 下载 {Name}（{Source}）", file.Name, sourceKey);
            await DownloadFileAsync(file.Url, temp, transfers.MaxFileBytes, ct);
            await using var stream = File.OpenRead(temp);
            var record = await transfers.CreateAsync(stream, file.Name, owner, ct);
            registry.Stage(kind, record.Id, record.FileName, record.Size, sourceKey);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private async Task<ExamGitHubRelease?> FetchLatestAsync(string repository, CancellationToken ct)
    {
        if (!ExamReleaseAssets.IsRepository(repository))
            throw new InvalidOperationException("Release 仓库名无效。");
        lock (_cacheLock)
        {
            if (_cached.TryGetValue(repository, out var hit) && hit.ExpiresAt > DateTimeOffset.UtcNow)
                return hit.Release;
        }

        // 走公开的 releases/latest 跳转，不调用 api.github.com。未登录的接口每小时只有 60 次，页面刷新就会用完。
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://github.com/{repository}/releases/latest");
        using var response = await Latest.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!IsRedirect(response.StatusCode))
        {
            ExplainGitHubStatus(response.StatusCode);
            return null;
        }
        var location = response.Headers.Location;
        if (location is null) return null;
        if (!location.IsAbsoluteUri)
            location = new Uri(new Uri($"https://github.com/{repository}/releases/latest"), location);
        var release = ExamReleaseAssets.ReleaseForTag(repository, ExamReleaseAssets.TagFromLatestLocation(location));
        if (release is null) return null;
        lock (_cacheLock)
            _cached[repository] = (release, DateTimeOffset.UtcNow.AddMinutes(15));
        return release;
    }

    private static async Task DownloadFileAsync(string url, string target, long maxBytes, CancellationToken ct)
    {
        using var response = await Download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        ExplainGitHubStatus(response.StatusCode);
        if (response.Content.Headers.ContentLength is > 0 and var length && length > maxBytes)
            throw new FileTransferException(ApiErrorCodes.InvalidRequest, "Release 文件超过文件中转上限。");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            if (read == 0) break;
            total += read;
            if (total > maxBytes)
                throw new FileTransferException(ApiErrorCodes.InvalidRequest, "Release 文件超过文件中转上限。");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total == 0) throw new FileTransferException(ApiErrorCodes.InvalidRequest, "Release 文件是空的。");
    }

    private static string? Describe(ExamGitHubRelease? release, ExamReleaseFile? file) =>
        release is null ? null : file is null ? $"{release.Tag}（没有可安装的文件）" : $"{release.Tag} · {file.Name}";

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static void ExplainGitHubStatus(HttpStatusCode status)
    {
        if (status is HttpStatusCode.Forbidden or (HttpStatusCode)429)
            throw new FileTransferException(ApiErrorCodes.InvalidRequest, "GitHub 限制了访问次数，请过几分钟再安装。");
        if (status == HttpStatusCode.NotFound)
            throw new FileTransferException(ApiErrorCodes.NotFound, "没有找到对应的 GitHub Release 文件。");
        if ((int)status is < 200 or >= 300)
            throw new FileTransferException(ApiErrorCodes.InvalidRequest, $"GitHub 返回了 {(int)status}，暂时无法下载。");
    }

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RemoteCI-Server");
        return client;
    }

    private static HttpClient CreateLatestClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RemoteCI-Server");
        return client;
    }
}

public sealed record ExamReleasePreview(string? App, string? Plugin, string? Error);

public sealed record ExamGitHubRelease(string Tag, IReadOnlyList<ExamReleaseFile> Assets);

public sealed record ExamReleaseFile(string Name, string Url, long Size);

/// <summary>从 Release 附件里挑出 Windows 安装包和 ExamAware 插件包。</summary>
public static class ExamReleaseAssets
{
    public static bool IsRepository(string repository) =>
        repository.Count(ch => ch == '/') == 1 && repository.Split('/') is [var owner, var name] &&
        IsSegment(owner) && IsSegment(name);

    public static ExamReleaseFile? SelectWindowsInstaller(IEnumerable<ExamReleaseFile> assets)
    {
        var setups = assets.Where(asset =>
            asset.Name.EndsWith("-setup.exe", StringComparison.OrdinalIgnoreCase)).ToList();
        return setups.FirstOrDefault(asset => !asset.Name.Contains("ia32", StringComparison.OrdinalIgnoreCase))
            ?? setups.FirstOrDefault();
    }

    public static ExamReleaseFile? SelectPluginPackage(IEnumerable<ExamReleaseFile> assets) =>
        assets.FirstOrDefault(asset => asset.Name.EndsWith(".ea2x", StringComparison.OrdinalIgnoreCase));

    public static string? TagFromLatestLocation(Uri location)
    {
        var parts = location.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5 || parts[2] != "releases" || parts[3] != "tag") return null;
        return Uri.UnescapeDataString(parts[4]);
    }

    public static string? VersionFromTag(string? tag)
    {
        var version = tag?.Trim().TrimStart('v', 'V') ?? "";
        if (version.Length is 0 or > 64 || version.Contains("..", StringComparison.Ordinal)) return null;
        return version.All(value => char.IsAsciiLetterOrDigit(value) || value is '.' or '-' or '_') ? version : null;
    }

    /// <summary>按公开的文件名规则拼出下载地址，不再请求 api.github.com。</summary>
    public static ExamGitHubRelease? ReleaseForTag(string repository, string? tag)
    {
        var version = VersionFromTag(tag);
        if (version is null || !IsRepository(repository) || string.IsNullOrEmpty(tag)) return null;
        var name = repository.Equals(ExamReleaseService.PluginRepository, StringComparison.OrdinalIgnoreCase)
            ? $"examaware-plugin-remoteci-{version}.ea2x"
            : $"ExamAware-{version}-setup.exe";
        var url = $"https://github.com/{repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";
        return new ExamGitHubRelease(tag, [new ExamReleaseFile(name, url, 0)]);
    }

    private static bool IsSegment(string value) =>
        value.Length > 0 && value is not "." and not ".." && value.All(IsName);

    private static bool IsName(char value) => char.IsAsciiLetterOrDigit(value) || value is '.' or '_' or '-';
}
