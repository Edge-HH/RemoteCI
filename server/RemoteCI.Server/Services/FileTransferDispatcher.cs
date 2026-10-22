using RemoteCI.Shared;
using RemoteCI.Shared.Models;

namespace RemoteCI.Server.Services;

/// <summary>
/// 把一个已上传的中转文件下发到多个班级/设备：先登记目标设备，再并行下发 SendFile 命令。
/// 插件收到命令后立即回执并在后台下载，之后的进度经 file_transfer_progress 写回 <see cref="FileTransferService"/>。
/// 不支持 file.transfer 的旧插件，在文件不超过 10 MB 且由系统管理员发起时改用内联方式。
/// </summary>
public sealed class FileTransferDispatcher(FileTransferService transfers, DeviceInventoryService devices, PeerRegistry peers)
{
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    public const string OutdatedPluginMessage = "设备上的 RemoteCI 插件版本过旧，不支持大文件分发，请先升级插件";

    public async Task<FileTransferView> SendAsync(
        FileTransferRecord record,
        IReadOnlyCollection<Guid> classIds,
        IReadOnlyCollection<Guid> groupIds,
        IReadOnlyCollection<Guid> connectionIds,
        FileTargetFolder folder,
        bool overwrite,
        Func<DeviceInventory, UserProfile> requestedBy,
        CancellationToken ct = default)
    {
        var plan = await devices.ResolveAsync(classIds, groupIds, connectionIds, ct);
        foreach (var failure in plan.Failures)
            transfers.AddDelivery(record, new FileTransferDelivery
            {
                CredentialId = Guid.NewGuid(),
                ClassId = failure.ClassId,
                TargetName = failure.DeviceName,
                State = FileTransferState.Failed,
                Message = failure.Message,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        await Task.WhenAll(plan.Targets.Select(target =>
            SendOneAsync(record, target, folder, overwrite, requestedBy(target), ct)));
        return transfers.ToView(record);
    }

    private async Task SendOneAsync(
        FileTransferRecord record, DeviceInventory target, FileTargetFolder folder, bool overwrite,
        UserProfile requestedBy, CancellationToken ct)
    {
        transfers.AddDelivery(record, new FileTransferDelivery
        {
            CredentialId = target.CredentialId,
            ClassId = target.ClassId,
            TargetName = target.Assigned ? target.ClassName : target.DeviceName,
            State = FileTransferState.Pending,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        if (target.ConnectionId is not { } connectionId)
        {
            await Fail("设备已离线");
            return;
        }

        var supportsTransfer = target.Capabilities.Contains(RemoteCiCapabilities.FileTransfer, StringComparer.Ordinal);
        FileDistributionRequest request;
        if (supportsTransfer)
        {
            request = new FileDistributionRequest
            {
                FileName = record.FileName,
                TransferId = record.Id,
                DownloadKey = record.DownloadKey,
                Size = record.Size,
                Sha256 = record.Sha256,
                TargetFolder = folder,
                Overwrite = overwrite,
            };
        }
        else if (record.Size <= FileDistributionRequest.MaxFileBytes && requestedBy.Role == UserRole.Admin &&
                 target.Capabilities.Contains(RemoteCiCapabilities.FileDistribute, StringComparer.Ordinal))
        {
            request = new FileDistributionRequest
            {
                FileName = record.FileName,
                ContentBase64 = Convert.ToBase64String(await File.ReadAllBytesAsync(transfers.ContentPath(record.Id), ct)),
                TargetFolder = folder,
                Overwrite = overwrite,
            };
        }
        else
        {
            await Fail(OutdatedPluginMessage);
            return;
        }

        var result = await peers.SendCommandAndWaitToConnectionAsync(new CommandMessage
        {
            Command = CommandKind.SendFile,
            ClassId = target.ClassId,
            FileDistribution = request,
            RequestedBy = requestedBy,
        }, connectionId, CommandTimeout, ct);

        await transfers.UpdateDeliveryAsync(record, target.CredentialId, delivery =>
        {
            if (!result.Success)
            {
                delivery.State = FileTransferState.Failed;
                delivery.Message = result.Message;
            }
            else if (!supportsTransfer)
            {
                // 内联方式的回执就是最终结果。
                delivery.State = FileTransferState.Completed;
                delivery.ReceivedBytes = record.Size;
                delivery.SavedPath = result.Data;
                delivery.Message = result.Message;
            }
            else if (delivery.State == FileTransferState.Pending)
            {
                delivery.State = FileTransferState.Downloading;
                delivery.Message = result.Message;
            }
        });

        Task Fail(string message) => transfers.UpdateDeliveryAsync(record, target.CredentialId, delivery =>
        {
            delivery.State = FileTransferState.Failed;
            delivery.Message = message;
        });
    }
}
