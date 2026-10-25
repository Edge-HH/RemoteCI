using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using RemoteCI.Plugin.Services;
using RemoteCI.Shared.Models;
using Xunit;

namespace RemoteCI.Plugin.Tests;

public sealed class ScreenCaptureServiceTests
{
    private static ScreenshotRequest Request(bool notify = false) => new()
    {
        Id = new string('a', 32),
        UploadKey = "key",
        Notify = notify,
        MaxWidth = 1920,
    };

    [Fact]
    public async Task CaptureAndUpload_EncodesPngUploadsAndNotifiesAfterUpload()
    {
        var frame = Frame(3, 2);
        var channel = new FakeChannel();
        var notified = false;
        var service = new ScreenCaptureService(null, new FakeGrabber(frame)) { Channel = channel };

        var result = await service.CaptureAndUploadAsync(Request(notify: true), () =>
        {
            Assert.NotNull(channel.Uploaded); // 提示必须在上传之后，避免出现在截图里。
            notified = true;
            return Task.CompletedTask;
        });

        Assert.True(result.Success, result.Message);
        Assert.True(notified);
        Assert.Equal(new string('a', 32), channel.Id);
        Assert.Equal("key", channel.Key);
        var data = JsonSerializer.Deserialize<ScreenshotResult>(result.Data!)!;
        Assert.Equal((3, 2, (long)channel.Uploaded!.Length), (data.Width, data.Height, data.Size));
        Assert.Equal(frame.Bgra, DecodeToBgra(channel.Uploaded!, 3, 2));
    }

    [Fact]
    public async Task CaptureAndUpload_SkipsNoticeWhenNotRequested()
    {
        var notified = false;
        var service = new ScreenCaptureService(null, new FakeGrabber(Frame(1, 1))) { Channel = new FakeChannel() };
        var result = await service.CaptureAndUploadAsync(Request(), () => { notified = true; return Task.CompletedTask; });
        Assert.True(result.Success);
        Assert.False(notified);
    }

    [Fact]
    public async Task CaptureAndUpload_ReportsCaptureAndUploadFailures()
    {
        var failing = new ScreenCaptureService(null, new FakeGrabber(null)) { Channel = new FakeChannel() };
        var captureFailed = await failing.CaptureAndUploadAsync(Request());
        Assert.False(captureFailed.Success);
        Assert.Equal("无法截取屏幕（测试）", captureFailed.Message);

        var rejected = new ScreenCaptureService(null, new FakeGrabber(Frame(1, 1))) { Channel = new FakeChannel("截图上传失败（HTTP 404）") };
        var uploadFailed = await rejected.CaptureAndUploadAsync(Request(notify: true), () => throw new InvalidOperationException("不应提示"));
        Assert.False(uploadFailed.Success);
        Assert.Equal("截图上传失败（HTTP 404）", uploadFailed.Message);

        var noChannel = new ScreenCaptureService(null, new FakeGrabber(Frame(1, 1)));
        Assert.False((await noChannel.CaptureAndUploadAsync(Request())).Success);
    }

    [Fact]
    public async Task CaptureAndUpload_RejectsInvalidRequest()
    {
        var service = new ScreenCaptureService(null, new FakeGrabber(Frame(1, 1))) { Channel = new FakeChannel() };
        Assert.Equal(CommandResultCodes.InvalidRequest, (await service.CaptureAndUploadAsync(null)).Code);
        var badId = Request();
        badId.Id = "../etc";
        Assert.Equal(CommandResultCodes.InvalidRequest, (await service.CaptureAndUploadAsync(badId)).Code);
        var badWidth = Request();
        badWidth.MaxWidth = 10;
        Assert.Equal(CommandResultCodes.InvalidRequest, (await service.CaptureAndUploadAsync(badWidth)).Code);
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1920, 1080)]
    [InlineData(3840, 2160, 1920, 1920, 1080)]
    [InlineData(3840, 1080, 1920, 1920, 540)]
    [InlineData(1280, 720, 1920, 1280, 720)]
    public void ScaledSize_KeepsAspectRatio(int width, int height, int maxWidth, int expectedWidth, int expectedHeight)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal((expectedWidth, expectedHeight), GdiScreenGrabber.ScaledSize(width, height, maxWidth));
    }

    private static CapturedScreen Frame(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = (byte)(i * 7);
            pixels[i + 1] = (byte)(i * 13 + 5);
            pixels[i + 2] = (byte)(255 - i);
            pixels[i + 3] = 255;
        }
        return new CapturedScreen(width, height, pixels);
    }

    /// <summary>独立解码：校验签名与 IHDR，解压 IDAT 并还原 Sub 滤波，返回 BGRA（Alpha 固定 255）。</summary>
    private static byte[] DecodeToBgra(byte[] png, int width, int height)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(width, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(height, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));

        using var idat = new MemoryStream();
        var offset = 8;
        while (offset < png.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            var type = System.Text.Encoding.ASCII.GetString(png, offset + 4, 4);
            if (type == "IDAT") idat.Write(png, offset + 8, length);
            offset += 12 + length;
        }
        idat.Position = 0;
        using var inflated = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionMode.Decompress)) zlib.CopyTo(inflated);
        var raw = inflated.ToArray();
        var stride = 1 + width * 3;
        Assert.Equal(stride * height, raw.Length);

        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Assert.Equal(1, raw[y * stride]);
            for (var x = 0; x < width; x++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var i = y * stride + 1 + x * 3 + c;
                    if (x > 0) raw[i] = (byte)(raw[i] + raw[i - 3]);
                }
                var src = y * stride + 1 + x * 3;
                var dst = (y * width + x) * 4;
                bgra[dst] = raw[src + 2];
                bgra[dst + 1] = raw[src + 1];
                bgra[dst + 2] = raw[src];
                bgra[dst + 3] = 255;
            }
        }
        return bgra;
    }

    private sealed class FakeGrabber(CapturedScreen? frame) : IScreenGrabber
    {
        public CapturedScreen Capture(int maxWidth) =>
            frame ?? throw new InvalidOperationException("无法截取屏幕（测试）");
    }

    private sealed class FakeChannel(string? error = null) : IScreenshotChannel
    {
        public string? Id { get; private set; }
        public string? Key { get; private set; }
        public byte[]? Uploaded { get; private set; }

        public Task<string?> UploadScreenshotAsync(string id, string uploadKey, byte[] png, CancellationToken ct)
        {
            Id = id;
            Key = uploadKey;
            if (error is null) Uploaded = png;
            return Task.FromResult(error);
        }
    }
}
