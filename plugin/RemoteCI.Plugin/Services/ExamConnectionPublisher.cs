using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RemoteCI.Plugin.Settings;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>
/// 把 RemoteCI 的服务器地址和考试绑定票据写到本机，供 ExamAware 插件读取。
/// 不写入插件长期凭据。配对码在配成长期凭据后会被清掉，之后只靠票据绑定。
/// </summary>
public sealed class ExamConnectionPublisher
{
    private readonly PluginSettings _settings;
    private readonly string _path;
    private readonly HttpClient _http;
    private string? _serverUrl;
    private string? _ticket;
    private Guid? _classId;
    private string? _className;

    public ExamConnectionPublisher(PluginSettings settings, string? path = null, HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteCI", "connection.json");
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public ILogger? Logger { get; set; }

    public void Publish()
    {
        var url = _settings.CloudServerUrl?.Trim() ?? string.Empty;
        if (!string.Equals(url, _serverUrl, StringComparison.OrdinalIgnoreCase))
        {
            _ticket = null;
            _classId = null;
            _className = null;
            _serverUrl = url;
        }
        Write();
    }

    public async Task RefreshTicketAsync(CancellationToken ct = default)
    {
        Publish();
        var url = _settings.CloudServerUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(_settings.CloudToken)) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url + "/api/exam/tickets");
            request.Headers.Authorization = new AuthenticationHeaderValue(Protocol.BearerScheme, _settings.CloudToken);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                Logger?.LogWarning("领取考试绑定票据失败：{Status}", (int)response.StatusCode);
                return;
            }
            var ticket = await response.Content.ReadFromJsonAsync<ExamTicketResponse>(JsonDefaults.Options, ct);
            if (ticket is null || string.IsNullOrWhiteSpace(ticket.Ticket)) return;
            _ticket = ticket.Ticket;
            _classId = ticket.ClassId;
            _className = ticket.ClassName;
            _serverUrl = url;
            Write();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Logger?.LogWarning(ex, "领取考试绑定票据失败");
        }
    }

    private void Write()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_serverUrl))
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var document = new
            {
                serverUrl = _serverUrl,
                pairCode = string.IsNullOrWhiteSpace(_settings.PluginPairCode) ? null : _settings.PluginPairCode.Trim(),
                lanPort = _settings.LanServerPort,
                ticket = _ticket,
                classId = _classId,
                className = _className,
                updatedAt = DateTimeOffset.UtcNow,
            };
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonDefaults.Options));
            File.Move(temp, _path, true);
            FileProtection.RestrictToCurrentUser(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger?.LogWarning(ex, "写入考试连接信息失败");
        }
    }
}
