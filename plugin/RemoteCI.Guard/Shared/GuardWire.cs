using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace RemoteCI.Guard;

// 守护云端通道（client=guard）的 WebSocket 消息格式，须与服务端 RemoteCI.Shared 的 Envelope 互通。
// 本文件被守护与插件共同编译，插件测试据此校验两边的序列化格式。

[DataContract]
internal class GuardEnvelope<T>
{
    [DataMember(Name = "protocolVersion")] public int ProtocolVersion { get; set; } = 3;
    [DataMember(Name = "type")] public string Type { get; set; } = string.Empty;
    [DataMember(Name = "messageId")] public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    [DataMember(Name = "replyToMessageId", EmitDefaultValue = false)] public string? ReplyToMessageId { get; set; }
    // DataContractJsonSerializer 会把 DateTimeOffset 写成 {"DateTime":"\/Date(..)\/"} 对象，服务端无法解析，
    // 也读不了服务端发来的 ISO 8601 字符串；因此按字符串收发。
    [DataMember(Name = "timestamp")] public string Timestamp { get; set; } = DateTimeOffset.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
    [DataMember(Name = "payload", EmitDefaultValue = false)] public T? Payload { get; set; }
}

[DataContract]
internal sealed class GuardPeerCapabilities
{
    [DataMember(Name = "softwareVersion")] public string SoftwareVersion { get; set; } = string.Empty;
    [DataMember(Name = "capabilities")] public List<string> Capabilities { get; set; } = new();
}

[DataContract]
internal sealed class GuardPolicyPayload
{
    [DataMember(Name = "enabled")] public bool Enabled { get; set; }
    [DataMember(Name = "enhancedMode")] public bool EnhancedMode { get; set; }
    [DataMember(Name = "restartAfterExit")] public bool RestartAfterExit { get; set; }
    [DataMember(Name = "restartAfterExitDelayMinutes")] public int RestartAfterExitDelayMinutes { get; set; }
    [DataMember(Name = "restartOnCrash")] public bool RestartOnCrash { get; set; }
    [DataMember(Name = "preventDeletion")] public bool PreventDeletion { get; set; }
    [DataMember(Name = "backupIntervalHours")] public int BackupIntervalHours { get; set; } = 24;
    [DataMember(Name = "randomBackupPath")] public bool RandomBackupPath { get; set; }
    [DataMember(Name = "backupDirectoryName")] public string BackupDirectoryName { get; set; } = string.Empty;
    [DataMember(Name = "processName")] public string ProcessName { get; set; } = GuardFiles.ProcessName;
    [DataMember(Name = "backupPaths")] public List<string> BackupPaths { get; set; } = new();
    [DataMember(Name = "organizationName")] public string? OrganizationName { get; set; }
}

[DataContract]
internal sealed class GuardCommand
{
    [DataMember(Name = "command")] public int Command { get; set; }
    [DataMember(Name = "requestedBy")] public GuardRequester? RequestedBy { get; set; }
}

[DataContract]
internal sealed class GuardRequester
{
    [DataMember(Name = "role")] public int Role { get; set; }
    [DataMember(Name = "permissions")] public int Permissions { get; set; }
}

[DataContract]
internal sealed class GuardCommandResult
{
    [DataMember(Name = "success")] public bool Success { get; set; }
    [DataMember(Name = "code")] public string Code { get; set; } = string.Empty;
    [DataMember(Name = "message")] public string Message { get; set; } = string.Empty;

    public static GuardCommandResult Failure(string code, string message) => new() { Success = false, Code = code, Message = message };
    public static GuardCommandResult Ok(string message) => new() { Success = true, Code = "OK", Message = message };
}

[DataContract]
internal sealed class GuardEventBatch
{
    [DataMember(Name = "events")] public List<GuardEventLine> Events { get; set; } = new();
}
