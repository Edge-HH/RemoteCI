using System.Net;
using System.Security.Cryptography;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class FileReceiveServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remoteci-file-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Desktop => Path.Combine(_root, "Desktop");

    private FileReceiveService CreateService(FakeChannel channel) =>
        new(null, _ => Desktop, Path.Combine(_root, "staging"))
        {
            Channel = channel,
            ProgressInterval = TimeSpan.Zero,
            RetryDelay = _ => TimeSpan.Zero,
            FinalReportRetryDelay = TimeSpan.Zero,
        };

    private static FileDistributionRequest Request(byte[] content, string name = "课件.pptx", string? sha = null) => new()
    {
        FileName = name,
        TransferId = Guid.NewGuid().ToString("N"),
        DownloadKey = "download-key-0123456789",
        Size = content.Length,
        Sha256 = sha ?? Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(),
        TargetFolder = FileTargetFolder.Desktop,
    };

    [Fact]
    public async Task Transfer_ResumesAfterInterruptionAndVerifiesHash()
    {
        var content = new byte[200_000];
        new Random(3).NextBytes(content);
        var channel = new FakeChannel(content) { BreakFirstResponseAfter = 70_000 };
        var service = CreateService(channel);

        var result = await service.SaveAsync(Request(content));
        Assert.True(result.Success, result.Message);
        await service.LastTransfer!;

        Assert.Equal([0L, 70_000L], channel.Offsets);
        var saved = Path.Combine(Desktop, "课件.pptx");
        Assert.Equal(content, await File.ReadAllBytesAsync(saved));
        var final = channel.Reports[^1];
        Assert.Equal(FileTransferState.Completed, final.State);
        Assert.Equal(saved, final.SavedPath);
        Assert.Contains(channel.Reports, x => x.State == FileTransferState.Downloading && x.ReceivedBytes is > 0 and < 200_000);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, "staging")));
    }

    [Fact]
    public async Task Transfer_DoesNotOverwriteExistingFile()
    {
        var content = "new"u8.ToArray();
        Directory.CreateDirectory(Desktop);
        await File.WriteAllTextAsync(Path.Combine(Desktop, "a.txt"), "old");
        var channel = new FakeChannel(content);
        var service = CreateService(channel);

        await service.SaveAsync(Request(content, "a.txt"));
        await service.LastTransfer!;

        Assert.Equal("old", await File.ReadAllTextAsync(Path.Combine(Desktop, "a.txt")));
        Assert.Equal("new", await File.ReadAllTextAsync(Path.Combine(Desktop, "a (2).txt")));
    }

    [Fact]
    public async Task Transfer_HashMismatchFailsWithoutSaving()
    {
        var content = "payload"u8.ToArray();
        var channel = new FakeChannel(content);
        var service = CreateService(channel);

        await service.SaveAsync(Request(content, sha: new string('0', 64)));
        await service.LastTransfer!;

        Assert.Equal(FileTransferState.Failed, channel.Reports[^1].State);
        Assert.Contains("校验失败", channel.Reports[^1].Message);
        Assert.False(Directory.Exists(Desktop) && Directory.EnumerateFiles(Desktop).Any());
    }

    [Fact]
    public async Task Transfer_ExpiredOnServerFailsWithoutRetry()
    {
        var channel = new FakeChannel([1, 2, 3]) { Status = HttpStatusCode.NotFound };
        var service = CreateService(channel);

        await service.SaveAsync(Request([1, 2, 3]));
        await service.LastTransfer!;

        Assert.Single(channel.Offsets);
        Assert.Equal(FileTransferState.Failed, channel.Reports[^1].State);
        Assert.Contains("过期", channel.Reports[^1].Message);
    }

    [Fact]
    public async Task Transfer_RejectsInvalidRequestAndMissingChannel()
    {
        var service = new FileReceiveService(null, _ => Desktop, Path.Combine(_root, "staging"));
        var request = Request([1]);
        Assert.Equal(CommandResultCodes.PluginOffline, (await service.SaveAsync(request)).Code);

        service.Channel = new FakeChannel([1]);
        request.Sha256 = "not-a-hash";
        Assert.Equal(CommandResultCodes.InvalidRequest, (await service.SaveAsync(request)).Code);
    }

    [Fact]
    public void SendFile_RequiresSendFilesPermission()
    {
        Assert.Equal(UserPermissions.SendFiles, CommandPermissions.Required(CommandKind.SendFile));
        Assert.True(UserPermissions.All.HasFlag(UserPermissions.SendFiles));
        Assert.True(RolePermissions.Assignable.HasFlag(UserPermissions.SendFiles));
    }

    private sealed class FakeChannel(byte[] content) : IFileTransferChannel
    {
        public long? BreakFirstResponseAfter { get; init; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public List<long> Offsets { get; } = [];
        public List<FileTransferProgress> Reports { get; } = [];

        public Task<HttpResponseMessage> RequestFileAsync(string transferId, string downloadKey, long offset, CancellationToken ct)
        {
            Offsets.Add(offset);
            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));
            var remaining = content[(int)offset..];
            Stream body = Offsets.Count == 1 && BreakFirstResponseAfter is { } limit
                ? new BreakingStream(remaining, (int)limit)
                : new MemoryStream(remaining);
            return Task.FromResult(new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new StreamContent(body),
            });
        }

        public Task<bool> ReportFileProgressAsync(FileTransferProgress progress)
        {
            lock (Reports) Reports.Add(progress);
            return Task.FromResult(true);
        }
    }

    /// <summary>读到指定字节后抛出 IOException，模拟网络中断。</summary>
    private sealed class BreakingStream(byte[] data, int breakAfter) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Clamp(count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            await base.ReadAsync(buffer[..Clamp(buffer.Length)], ct);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private int Clamp(int count)
        {
            if (Position >= breakAfter) throw new IOException("连接被重置");
            return (int)Math.Min(count, breakAfter - Position);
        }
    }
}
