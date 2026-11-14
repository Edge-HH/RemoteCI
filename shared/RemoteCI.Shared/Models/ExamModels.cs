using System.Text.Json.Serialization;

namespace RemoteCI.Shared.Models;

/// <summary>服务端中转的一个 ExamAware 安装包、插件包或考试档案。</summary>
public sealed class ExamFileRef
{
    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("transferId")]
    public string TransferId { get; set; } = string.Empty;

    [JsonPropertyName("downloadKey")]
    public string DownloadKey { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    public FileDistributionRequest ToTransferRequest() => new()
    {
        FileName = FileName,
        TransferId = TransferId,
        DownloadKey = DownloadKey,
        Size = Size,
        Sha256 = Sha256,
    };

    public string? Validate() => FileDistributionRequest.ValidateTransfer(ToTransferRequest());
}

/// <summary>InstallExamAware 命令参数。</summary>
public sealed class ExamInstallRequest
{
    [JsonPropertyName("installer")]
    public ExamFileRef? Installer { get; set; }

    /// <summary>与安装程序一起放入 ExamAware 插件目录的插件包；installPlugin 为真时必填。</summary>
    [JsonPropertyName("plugin")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExamFileRef? Plugin { get; set; }

    [JsonPropertyName("installPlugin")]
    public bool InstallPlugin { get; set; }

    [JsonPropertyName("launchAfter")]
    public bool LaunchAfter { get; set; }
}

/// <summary>DistributeExam 命令参数。插件把档案保存为 localFileName，ExamAware 插件按同一文件名放映。</summary>
public sealed class ExamDistributeRequest
{
    [JsonPropertyName("jobId")]
    public Guid JobId { get; set; }

    [JsonPropertyName("localFileName")]
    public string LocalFileName { get; set; } = string.Empty;

    [JsonPropertyName("exam")]
    public ExamFileRef? Exam { get; set; }

    public static string FileNameFor(Guid jobId) => jobId.ToString("N") + ".ea2";

    /// <summary>只接受 32 位十六进制加 .ea2，避免把档案写到考试目录之外。</summary>
    public static bool IsLocalFileName(string? name) =>
        name is not null && name.Length == 36 && name.EndsWith(".ea2", StringComparison.OrdinalIgnoreCase) &&
        name[..32].All(Uri.IsHexDigit);
}

/// <summary>ExamAware 插件用教室端 RemoteCI 写出的票据或仍有效的配对码登记为本班考试端。</summary>
public sealed class ExamBindRequest
{
    [JsonPropertyName("ticket")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Ticket { get; set; }

    [JsonPropertyName("pairCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PairCode { get; set; }

    [JsonPropertyName("deviceName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeviceName { get; set; }
}

public sealed class ExamBindResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("classId")]
    public Guid ClassId { get; set; }

    [JsonPropertyName("className")]
    public string ClassName { get; set; } = string.Empty;
}

/// <summary>教室端 RemoteCI 插件连接后领取的考试绑定票据，只写入本机连接信息，不代替插件凭据。</summary>
public sealed class ExamTicketResponse
{
    [JsonPropertyName("ticket")]
    public string Ticket { get; set; } = string.Empty;

    [JsonPropertyName("classId")]
    public Guid ClassId { get; set; }

    [JsonPropertyName("className")]
    public string ClassName { get; set; } = string.Empty;

    [JsonPropertyName("expiresAt")]
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class ExamJobView
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("localFileName")]
    public string LocalFileName { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = "pending";
}

public sealed class ExamJobAck
{
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Message { get; set; }
}
