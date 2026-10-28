using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// ClassIsland 官方插件市场索引的服务端副本：与宿主 PluginMarketService 读取同一个 index.zip（index.v2.json），
/// 供 WebUI 展示插件名称、Id、下载量与星标，并在“服务端预下载”时解析下载地址、MD5 与必选依赖。
/// 索引在内存中缓存一段时间；刷新失败时继续使用上一次成功的副本。
/// </summary>
public sealed class PluginMarketCatalog(
    IHttpClientFactory httpClients,
    IOptions<ServerOptions> options,
    TimeProvider time,
    ILogger<PluginMarketCatalog> logger)
{
    public const string HttpClientName = "PluginMarket";
    public const string DefaultIndexUrl = "https://get.classisland.tech/d/ClassIsland-Ningbo-S3/classisland/plugin/index.zip?time={time}";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
    private static readonly Version MinimumApiVersion = new(2, 0, 0, 0);
    private static readonly JsonSerializerOptions IndexJson = new() { PropertyNameCaseInsensitive = true };

    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private MarketIndex? _cached;

    /// <summary>读取插件市场；缓存过期或要求刷新时重新下载索引。</summary>
    public async Task<MarketIndex> GetAsync(bool refresh = false, CancellationToken ct = default)
    {
        if (!refresh && _cached is { } fresh && time.GetUtcNow() - fresh.FetchedAt < CacheDuration) return fresh;
        await _refreshLock.WaitAsync(ct);
        try
        {
            if (!refresh && _cached is { } again && time.GetUtcNow() - again.FetchedAt < CacheDuration) return again;
            try
            {
                _cached = await DownloadAsync(ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or TaskCanceledException)
            {
                logger.LogWarning(ex, "读取 ClassIsland 插件市场索引失败");
                if (_cached is null) throw new PluginMarketException($"无法读取 ClassIsland 插件市场：{ex.Message}");
            }
            return _cached;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// 解析要安装的插件及其必选依赖（递归），依赖排在被依赖者之前；返回 (插件, 是否仅作为依赖)。
    /// 市场里找不到的插件或必选依赖会抛出 <see cref="PluginMarketException"/>。
    /// </summary>
    public static IReadOnlyList<(MarketPlugin Plugin, bool IsDependency)> ResolveInstallSet(
        MarketIndex index, IReadOnlyCollection<string> requestedIds)
    {
        var ordered = new List<(MarketPlugin, bool)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        var requested = new HashSet<string>(requestedIds, StringComparer.OrdinalIgnoreCase);

        void Visit(string id, bool required)
        {
            if (!visited.Add(id)) return;
            if (index.Find(id) is not { } plugin)
            {
                if (required) missing.Add(id);
                return;
            }
            foreach (var dependency in plugin.Dependencies)
                Visit(dependency.Id, dependency.IsRequired);
            ordered.Add((plugin, !requested.Contains(plugin.Id)));
        }

        foreach (var id in requestedIds) Visit(id, required: true);
        if (missing.Count > 0)
            throw new PluginMarketException($"插件市场中找不到：{string.Join("、", missing)}");
        return ordered;
    }

    private async Task<MarketIndex> DownloadAsync(CancellationToken ct)
    {
        var template = string.IsNullOrWhiteSpace(options.Value.PluginMarketIndexUrl)
            ? DefaultIndexUrl
            : options.Value.PluginMarketIndexUrl;
        var url = template.Replace("{time}", time.GetUtcNow().ToUnixTimeSeconds().ToString(), StringComparison.Ordinal);
        using var response = await httpClients.CreateClient(HttpClientName).GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct);
        buffer.Position = 0;
        return Parse(buffer, options.Value.PluginMarketMirror, time.GetUtcNow());
    }

    /// <summary>解析 index.zip；与宿主一致，只保留 API 版本 2.0 及以上的插件，并把 {root} 替换为镜像地址。</summary>
    public static MarketIndex Parse(Stream indexZip, string? preferredMirror, DateTimeOffset fetchedAt)
    {
        using var archive = new ZipArchive(indexZip, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry("index.v2.json") ?? throw new InvalidDataException("插件市场索引缺少 index.v2.json");
        using var stream = entry.Open();
        var raw = JsonSerializer.Deserialize<RawIndex>(stream, IndexJson) ?? throw new InvalidDataException("插件市场索引为空");
        var mirrors = raw.DownloadMirrors ?? [];
        // 依次尝试：配置的镜像、github、其余镜像；下载插件包时逐个回退。
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(preferredMirror) && mirrors.TryGetValue(preferredMirror, out var preferred)) roots.Add(preferred);
        if (mirrors.TryGetValue("github", out var github)) roots.Add(github);
        roots.AddRange(mirrors.Values);
        roots = roots.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.TrimEnd('/')).Distinct(StringComparer.Ordinal).ToList();
        if (roots.Count == 0) roots.Add("https://github.com");

        var plugins = new List<MarketPlugin>();
        foreach (var item in raw.Plugins ?? [])
        {
            var manifest = item.Manifest;
            if (manifest is null || !PluginPackageManifest.IsValidId(manifest.Id) || string.IsNullOrWhiteSpace(item.DownloadUrl)) continue;
            if (!Version.TryParse(manifest.ApiVersion, out var api) || api < MinimumApiVersion) continue;
            plugins.Add(new MarketPlugin(
                manifest.Id!,
                string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id! : manifest.Name,
                manifest.Description ?? string.Empty,
                manifest.Author ?? string.Empty,
                manifest.Version ?? string.Empty,
                item.DownloadCount,
                item.StarsCount,
                Resolve(item.RealIconPath, roots[0]),
                manifest.Url,
                roots.Select(root => item.DownloadUrl!.Replace("{root}", root, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList(),
                item.DownloadMd5 ?? string.Empty,
                (manifest.Dependencies ?? [])
                    .Where(x => PluginPackageManifest.IsValidId(x.Id))
                    .Select(x => new MarketDependency(x.Id!, x.IsRequired))
                    .ToList()));
        }
        return new MarketIndex(
            plugins.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(x => x.First())
                .OrderByDescending(x => x.DownloadCount).ThenBy(x => x.Name, StringComparer.CurrentCulture).ToList(),
            fetchedAt);
    }

    private static string? Resolve(string? value, string root) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Replace("{root}", root, StringComparison.Ordinal);

    private sealed class RawIndex
    {
        public List<RawItem>? Plugins { get; set; }
        public Dictionary<string, string>? DownloadMirrors { get; set; }
    }

    private sealed class RawItem
    {
        public string? DownloadUrl { get; set; }
        public string? DownloadMd5 { get; set; }
        public RawManifest? Manifest { get; set; }
        public string? RealIconPath { get; set; }
        public long DownloadCount { get; set; }
        public long StarsCount { get; set; }
    }

    private sealed class RawManifest
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Author { get; set; }
        public string? Version { get; set; }
        public string? ApiVersion { get; set; }
        public string? Url { get; set; }
        public List<RawDependency>? Dependencies { get; set; }
    }

    private sealed class RawDependency
    {
        public string? Id { get; set; }
        public bool IsRequired { get; set; }
    }
}

public sealed record MarketIndex(IReadOnlyList<MarketPlugin> Plugins, DateTimeOffset FetchedAt)
{
    public MarketPlugin? Find(string id) =>
        Plugins.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>插件市场中的一个插件；DownloadUrls 按镜像优先级排列。</summary>
public sealed record MarketPlugin(
    string Id,
    string Name,
    string Description,
    string Author,
    string Version,
    long DownloadCount,
    long StarsCount,
    string? IconUrl,
    string? Url,
    [property: JsonIgnore] IReadOnlyList<string> DownloadUrls,
    [property: JsonIgnore] string DownloadMd5,
    IReadOnlyList<MarketDependency> Dependencies);

public sealed record MarketDependency(string Id, bool IsRequired);

public sealed class PluginMarketException(string message) : Exception(message);
