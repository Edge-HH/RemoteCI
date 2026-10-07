using System.IO.Compression;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RemoteCI.Shared;

namespace RemoteCI.Server.Services;

/// <summary>GitHub 上发布的一个 release 及其附件。</summary>
public sealed record ReleaseInfo(
    string Tag,
    string Name,
    string Body,
    IReadOnlyList<ReleaseAsset> Assets,
    bool Prerelease = false,
    bool Draft = false);

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

public enum UpdateApplyMode
{
    ManagedByPlatform,
    InProcessContainer,
    ExternalInstaller,
}

public enum UpdateChannel
{
    Stable,
    Beta,
}

public sealed record PreparedUpdate(
    string StagingDirectory,
    string ExtractedDirectory);

/// <summary>
/// 服务端更新：从 GitHub 仓库最新 release 拉取当前平台的服务端包，
/// Docker 内可就地覆盖后由重启策略拉起；Windows 和裸机 Linux 由独立更新进程在
/// 当前进程退出后替换文件并重新启动，避免 Windows 原生 DLL 文件锁。
/// </summary>
public sealed class UpdateService
{
    public const string FnosManagedMessage = "由 fnOS 应用中心管理，请从 GitHub Releases 下载 FPK 手动升级。";
    public const string DevelopmentManagedMessage = "开发环境由 Visual Studio 或 dotnet build 管理，已禁用 WebUI 覆盖更新。";
    private const string Repo = "Edge-HH/RemoteCI";
    private const string ReleasesApiUrl = $"https://api.github.com/repos/{Repo}/releases?per_page=20";
    private static readonly string UserAgent = $"RemoteCI-Server/{AppVersion.Version}";

    private static readonly HttpClient Http = CreateHttpClient();
    private readonly string[] _serverArguments;
    private readonly SemaphoreSlim _prepareGate = new(1, 1);

    /// <summary>稳定版使用 ClassIsland 要求的四段纯数字标签，Beta 保留 v3.x.x-beta.y 标签。</summary>
    private static readonly Regex StableVersionTagPattern = new(
        @"^3\.[0-9]+\.[0-9]+\.[0-9]+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly Regex BetaVersionTagPattern = new(
        @"^v3\.[0-9]+\.[0-9]+-beta\.[1-9][0-9]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public UpdateService(string[]? serverArguments = null) => _serverArguments = serverArguments ?? [];

    /// <summary>
    /// 是否运行在飞牛 fnOS 应用环境。fpk 的 docker-compose 会注入
    /// <c>REMOTECI_RUNTIME=fnos</c>；该平台通过 fnOS 应用中心安装 GitHub Release 中的 FPK。
    /// </summary>
    public static bool IsFnosRuntime =>
        string.Equals(
            Environment.GetEnvironmentVariable("REMOTECI_RUNTIME"),
            "fnos",
            StringComparison.OrdinalIgnoreCase);

    public static bool IsContainerRuntime =>
        string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);

    public static UpdateApplyMode DetermineApplyMode(bool isFnos, bool isContainer) =>
        isFnos
            ? UpdateApplyMode.ManagedByPlatform
            : isContainer
                ? UpdateApplyMode.InProcessContainer
                : UpdateApplyMode.ExternalInstaller;

    public static UpdateApplyMode CurrentApplyMode =>
        DetermineApplyMode(IsFnosRuntime, IsContainerRuntime);

    /// <summary>
    /// 开发环境的 ContentRoot 通常就是源码目录，绝不能让 release 覆盖；
    /// fnOS 则必须继续由应用中心管理，不能由容器内进程覆盖安装文件。
    /// </summary>
    public static bool CanSelfUpdate(bool isDevelopment, bool isFnos) =>
        !isDevelopment && !isFnos;

    /// <summary>
    /// 正式部署只替换实际运行程序集所在目录，不使用可能指向源码或外部内容目录的 ContentRoot。
    /// </summary>
    public static string ResolveInstallDirectory(string applicationBaseDirectory) =>
        Path.GetFullPath(applicationBaseDirectory);

    public string CurrentVersion => AppVersion.Version;

    /// <summary>正式渠道排除预发布版；Beta 渠道同时接收正式版与预发布版，但都不得跨协议主版本。</summary>
    public static ReleaseInfo? SelectReleaseForChannel(
        IEnumerable<ReleaseInfo> releases,
        UpdateChannel channel) =>
        releases
            .Where(release =>
                !release.Draft &&
                (channel == UpdateChannel.Stable
                    ? IsCanonicalStableRelease(release.Tag) && !release.Prerelease
                    : (IsCanonicalStableRelease(release.Tag) && !release.Prerelease)
                        || (IsBetaRelease(release.Tag) && release.Prerelease)))
            .MaxBy(
                release => release.Tag,
                Comparer<string>.Create((left, right) => CompareVersions(left, right)));

    public static bool IsCanonicalStableRelease(string tag) => StableVersionTagPattern.IsMatch(tag);

    public static bool IsBetaRelease(string tag) => BetaVersionTagPattern.IsMatch(tag);

    public static bool IsCurrentProtocolRelease(string tag) =>
        IsCanonicalStableRelease(tag) || IsBetaRelease(tag);

    /// <summary>按更新渠道拉取最新 release；仓库暂无符合条件的版本时返回 null。</summary>
    public async Task<ReleaseInfo?> FetchLatestReleaseAsync(UpdateChannel channel, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
        var releases = document.RootElement.EnumerateArray()
            .Select(ParseRelease)
            .OfType<ReleaseInfo>()
            .Where(release => !string.IsNullOrWhiteSpace(release.Tag))
            .ToList();
        return SelectReleaseForChannel(releases, channel);
    }

    private static ReleaseInfo? ParseRelease(JsonElement root)
    {
        if (!root.TryGetProperty("tag_name", out var tagElement)) return null;
        var assets = root.TryGetProperty("assets", out var assetsElement)
            ? assetsElement.EnumerateArray()
                .Select(asset => new ReleaseAsset(
                    asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "",
                    asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0))
                .Where(a => !string.IsNullOrEmpty(a.Name) && !string.IsNullOrEmpty(a.DownloadUrl))
                .ToList()
            : [];
        return new ReleaseInfo(
            tagElement.GetString() ?? "",
            root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            assets,
            root.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True,
            root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True);
    }

    /// <summary>挑选与当前运行平台匹配的服务端压缩包。</summary>
    public ReleaseAsset? SelectServerAsset(ReleaseInfo release)
    {
        var os = OperatingSystem.IsWindows() ? "win" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };
        var version = NormalizeVersion(release.Tag);
        return release.Assets.FirstOrDefault(asset =>
            asset.Name.StartsWith($"RemoteCI.Server-{version}-{os}-{arch}", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeVersion(string tag) => tag.TrimStart('v', 'V');

    /// <summary>比较语义版本：latest 更新时返回 true。</summary>
    public static bool IsNewer(string latest, string current) => CompareVersions(latest, current) > 0;

    /// <summary>比较语义版本，正式版在相同核心版本的预发布版之后。</summary>
    public static int CompareVersions(string left, string right)
    {
        var leftVersion = ParseVersion(left);
        var rightVersion = ParseVersion(right);
        for (var i = 0; i < Math.Max(leftVersion.Core.Length, rightVersion.Core.Length); i++)
        {
            var comparison = leftVersion.Core.ElementAtOrDefault(i)
                .CompareTo(rightVersion.Core.ElementAtOrDefault(i));
            if (comparison != 0) return comparison;
        }

        if (leftVersion.Prerelease.Length == 0) return rightVersion.Prerelease.Length == 0 ? 0 : 1;
        if (rightVersion.Prerelease.Length == 0) return -1;
        for (var i = 0; i < Math.Max(leftVersion.Prerelease.Length, rightVersion.Prerelease.Length); i++)
        {
            if (i >= leftVersion.Prerelease.Length) return -1;
            if (i >= rightVersion.Prerelease.Length) return 1;
            var leftPart = leftVersion.Prerelease[i];
            var rightPart = rightVersion.Prerelease[i];
            var leftNumeric = int.TryParse(leftPart, out var leftNumber);
            var rightNumeric = int.TryParse(rightPart, out var rightNumber);
            var comparison = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : leftNumeric != rightNumeric
                    ? leftNumeric ? -1 : 1
                    : string.Compare(leftPart, rightPart, StringComparison.Ordinal);
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    /// <summary>普通更新只允许升级；强制更新额外允许同版本覆盖，但不允许降级。</summary>
    public static bool CanInstall(string latest, string current, bool force)
    {
        var comparison = CompareVersions(latest, current);
        return comparison > 0 || force && comparison == 0;
    }

    private static (int[] Core, string[] Prerelease) ParseVersion(string value)
    {
        var withoutBuild = NormalizeVersion(value).Split('+', 2)[0];
        var segments = withoutBuild.Split('-', 2);
        var core = segments[0].Split('.').Select(part => int.TryParse(part, out var number) ? number : 0).ToArray();
        var prerelease = segments.Length == 2
            ? segments[1].Split('.', StringSplitOptions.RemoveEmptyEntries)
            : [];
        return (core, prerelease);
    }

    /// <summary>
    /// 下载并解压更新到数据库旁的暂存目录；此阶段不触碰运行文件。
    /// </summary>
    public async Task<PreparedUpdate> PrepareUpdateAsync(
        ReleaseInfo release,
        ReleaseAsset asset,
        string databasePath,
        string contentRoot,
        CancellationToken ct)
    {
        // 并发点击“开始更新”会竞争同一暂存目录与 .part 下载文件，串行化整个准备流程。
        await _prepareGate.WaitAsync(ct);
        try
        {
            // 只接受已验证的稳定/Beta tag，防止恶意 tag（含 ../ 等路径片段）把文件写到暂存目录之外。
            if (!IsCurrentProtocolRelease(release.Tag))
                throw new InvalidDataException($"Release tag 格式无效：{release.Tag}");

            var updatesRoot = GetUpdatesRoot(databasePath, contentRoot);
            var staging = Path.Combine(updatesRoot, NormalizeVersion(release.Tag));
            Directory.CreateDirectory(staging);

            // asset 名只取文件名部分，防御下载落盘路径穿越（GitHub API 正常不会返回带路径分隔符的名字）。
            // 下载按资产声明大小设上限（留 1 MiB 余量），防止恶意/异常响应无限写盘。
            var archive = Path.Combine(staging, Path.GetFileName(asset.Name));
            await DownloadAsync(asset.DownloadUrl, archive, asset.Size + 1024 * 1024, ct);
            await VerifyChecksumAsync(release, asset, archive, ct);

            var extracted = Path.Combine(staging, "extracted");
            if (Directory.Exists(extracted)) Directory.Delete(extracted, recursive: true);
            Directory.CreateDirectory(extracted);
            ZipFile.ExtractToDirectory(archive, extracted, overwriteFiles: true);

            var version = NormalizeVersion(release.Tag);
            ValidatePackageVersion(extracted, version);
            return new PreparedUpdate(staging, extracted);
        }
        finally
        {
            _prepareGate.Release();
        }
    }

    /// <summary>启动对应平台的安全应用流程；成功返回后调用方应平滑退出当前服务端。</summary>
    public async Task<UpdateApplyMode> BeginApplyAsync(
        PreparedUpdate update,
        string contentRoot,
        CancellationToken ct)
    {
        var mode = CurrentApplyMode;
        if (mode == UpdateApplyMode.ManagedByPlatform)
            throw new InvalidOperationException(FnosManagedMessage);

        if (mode == UpdateApplyMode.InProcessContainer)
        {
            await UpdateInstaller.ApplyFilesAsync(update.ExtractedDirectory, contentRoot, ct);
            // 容器内就地覆盖成功后清理暂存目录，避免更新包长期占用数据卷空间。
            TryDeleteStaging(update.StagingDirectory);
            return mode;
        }

        StartExternalInstaller(update, contentRoot);
        return mode;
    }

    /// <summary>更新包统一存放在数据库同级目录的 updates 子目录。</summary>
    public static string GetUpdatesRoot(string databasePath, string contentRoot)
    {
        var dataDir = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        return Path.Combine(dataDir is { Length: > 0 } ? dataDir : contentRoot, "updates");
    }

    /// <summary>尽力删除更新暂存目录；文件锁等原因失败时保留现场，不影响更新结果。</summary>
    internal static void TryDeleteStaging(string stagingDirectory)
    {
        try { Directory.Delete(stagingDirectory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清理失败不影响已完成的更新。
        }
    }

    /// <summary>
    /// 下载文件并强制校验累计字节数不超过 <paramref name="maxBytes"/>（不信任 Content-Length，
    /// 按实际读取量累计），超限即中断并删除部分文件。
    /// </summary>
    private static async Task DownloadAsync(string url, string target, long maxBytes, CancellationToken ct)
    {
        var partial = target + ".part";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        try
        {
            await using var output = File.Create(partial);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > maxBytes)
                    throw new InvalidDataException($"下载内容超出大小上限 {maxBytes} 字节，已中断。");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        catch
        {
            try { File.Delete(partial); } catch (IOException) { }
            throw;
        }
        File.Move(partial, target, overwrite: true);
    }

    /// <summary>
    /// 发布附带同名校验和文件（&lt;包名&gt;.zip.sha256，第一列为十六进制 SHA-256）时强制校验。
    /// 正式版（Stable 渠道）必须携带校验和资产，缺失即拒绝安装，避免发布流程漏传时
    /// 静默跳过完整性校验；Beta 预发布版保持向后兼容允许缺失。
    /// </summary>
    private async Task VerifyChecksumAsync(
        ReleaseInfo release, ReleaseAsset asset, string archivePath, CancellationToken ct)
    {
        var checksumAsset = release.Assets.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
        if (checksumAsset is null)
        {
            if (!release.Prerelease)
                throw new InvalidDataException($"正式版 {release.Tag} 缺少校验和资产 {asset.Name}.sha256，拒绝安装");
            return;
        }

        var checksumFile = Path.Combine(Path.GetDirectoryName(archivePath)!, Path.GetFileName(checksumAsset.Name));
        // 校验和文件固定 64KB 上限（十六进制 SHA-256 文本远小于此）。
        await DownloadAsync(checksumAsset.DownloadUrl, checksumFile, 64 * 1024, ct);
        var firstLine = (await File.ReadAllTextAsync(checksumFile, ct)).Trim();
        var expected = firstLine.Split(' ', 2)[0].Trim().ToLowerInvariant();
        await VerifyFileHashAsync(expected, archivePath, ct);
    }

    /// <summary>校验文件 SHA-256 与期望十六进制摘要一致，不一致即拒绝安装。</summary>
    internal static async Task VerifyFileHashAsync(string expectedHex, string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).ToLowerInvariant();
        var expected = expectedHex.Trim().ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual)))
            throw new InvalidDataException("更新包 SHA-256 校验失败，拒绝安装");
    }

    private void StartExternalInstaller(PreparedUpdate update, string contentRoot)
    {
        var runnerAssembly = Path.Combine(update.ExtractedDirectory, "RemoteCI.Server.dll");
        if (!File.Exists(runnerAssembly))
            throw new InvalidDataException("更新包缺少 RemoteCI.Server.dll，无法启动外部安装器。");

        var planPath = Path.Combine(update.StagingDirectory, "install-plan.json");
        var markerPath = Path.Combine(update.StagingDirectory, "started.marker");
        try { if (File.Exists(markerPath)) File.Delete(markerPath); } catch (IOException) { }
        var plan = new UpdateInstallPlan
        {
            SourceDirectory = update.ExtractedDirectory,
            DestinationDirectory = Path.GetFullPath(contentRoot),
            DotnetHostPath = ResolveDotnetHost(),
            ServerAssemblyPath = Path.Combine(Path.GetFullPath(contentRoot), "RemoteCI.Server.dll"),
            ServerArguments = _serverArguments,
            ServerProcessId = Environment.ProcessId,
            LogPath = Path.Combine(update.StagingDirectory, "update.log"),
            StartupMarkerPath = markerPath,
        };
        File.WriteAllText(planPath, JsonSerializer.Serialize(plan));

        var startInfo = new ProcessStartInfo(plan.DotnetHostPath)
        {
            WorkingDirectory = update.ExtractedDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(runnerAssembly);
        startInfo.ArgumentList.Add(UpdateInstaller.Command);
        startInfo.ArgumentList.Add(planPath);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动外部更新器，运行文件未被修改。");
    }

    private static void ValidatePackageVersion(string extractedDirectory, string expectedVersion)
    {
        var assembly = Path.Combine(extractedDirectory, "RemoteCI.Server.dll");
        if (!File.Exists(assembly)) throw new InvalidDataException("更新包缺少 RemoteCI.Server.dll。");
        var actual = FileVersionInfo.GetVersionInfo(assembly).ProductVersion?.Split('+', 2)[0];
        if (!string.Equals(actual, expectedVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"更新包版本不匹配：Release 为 {expectedVersion}，包内服务端为 {actual ?? "未知"}。");
    }

    private static string ResolveDotnetHost()
    {
        var current = Environment.ProcessPath;
        if (current is not null && string.Equals(
                Path.GetFileNameWithoutExtension(current), "dotnet", StringComparison.OrdinalIgnoreCase))
            return current;

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (File.Exists(candidate)) return candidate;
        }

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var candidate = Path.Combine(programFiles, "dotnet", "dotnet.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return "dotnet";
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
