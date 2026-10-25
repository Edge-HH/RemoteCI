using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RemoteCI.Shared.Models;

namespace RemoteCI.Plugin.Services;

/// <summary>截图上传通道；由云端客户端实现。</summary>
public interface IScreenshotChannel
{
    /// <summary>以插件凭据加一次性上传凭据上传 PNG；成功返回 null，否则返回中文错误说明。</summary>
    Task<string?> UploadScreenshotAsync(string id, string uploadKey, byte[] png, CancellationToken ct);
}

/// <summary>一帧屏幕像素：自上而下、每行 Width*4 字节的 BGRA。</summary>
internal sealed record CapturedScreen(int Width, int Height, byte[] Bgra);

internal interface IScreenGrabber
{
    /// <summary>截取全部显示器拼成的虚拟桌面；宽度超过 maxWidth 时等比缩小。失败时抛出 InvalidOperationException。</summary>
    CapturedScreen Capture(int maxWidth);
}

/// <summary>
/// 屏幕截图（CaptureScreen）：截取全部显示器、编码为 PNG 并上传到服务端，上传成功后才回执成功。
/// 截图先于教室端提示完成，提示本身不会出现在截图里。
/// </summary>
public sealed class ScreenCaptureService
{
    private readonly ILogger _logger;
    private readonly IScreenGrabber _grabber;

    public ScreenCaptureService(ILogger<ScreenCaptureService>? logger = null)
        : this(logger, OperatingSystem.IsWindows() ? new GdiScreenGrabber() : null)
    {
    }

    internal ScreenCaptureService(ILogger? logger, IScreenGrabber? grabber)
    {
        _logger = logger ?? NullLogger.Instance;
        _grabber = grabber ?? new UnsupportedScreenGrabber();
    }

    /// <summary>云端客户端建立后注入。</summary>
    public IScreenshotChannel? Channel { get; set; }

    /// <param name="notify">上传成功后在教室端显示提示；仅当请求要求提示时调用。</param>
    public async Task<CommandResult> CaptureAndUploadAsync(
        ScreenshotRequest? request, Func<Task>? notify = null, CancellationToken ct = default)
    {
        if (ScreenshotRequest.Validate(request) is { } error)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, error);
        if (Channel is not { } channel)
            return CommandResult.Failure(CommandResultCodes.InternalError, "云端连接未就绪，无法上传截图");

        byte[] png;
        CapturedScreen frame;
        try
        {
            (frame, png) = await Task.Run(() =>
            {
                var captured = _grabber.Capture(request!.MaxWidth);
                return (captured, PngWriter.Encode(captured.Width, captured.Height, captured.Bgra));
            }, ct);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "RemoteCI 屏幕截图失败");
            return CommandResult.Failure(CommandResultCodes.InternalError, ex.Message);
        }
        if (png.Length > ScreenshotRequest.MaxBytes)
            return CommandResult.Failure(CommandResultCodes.InvalidRequest, "截图超过大小上限，请降低截图宽度");

        if (await channel.UploadScreenshotAsync(request!.Id, request.UploadKey, png, ct) is { } uploadError)
            return CommandResult.Failure(CommandResultCodes.InternalError, uploadError);

        if (request.Notify && notify is not null)
        {
            try { await notify(); }
            catch (Exception ex) { _logger.LogWarning(ex, "RemoteCI 截图提示显示失败"); }
        }
        return new CommandResult
        {
            Success = true,
            Code = CommandResultCodes.Ok,
            Message = "截图已上传",
            Data = JsonSerializer.Serialize(new ScreenshotResult { Width = frame.Width, Height = frame.Height, Size = png.Length }),
        };
    }

    private sealed class UnsupportedScreenGrabber : IScreenGrabber
    {
        public CapturedScreen Capture(int maxWidth) =>
            throw new InvalidOperationException("当前系统不支持屏幕截图");
    }
}

/// <summary>最小 PNG 编码器：8 位 RGB、逐行 Sub 滤波、zlib 压缩；只依赖 BCL，不需要 Avalonia 渲染平台。</summary>
internal static class PngWriter
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] CrcTable = BuildCrcTable();

    public static byte[] Encode(int width, int height, byte[] bgra)
    {
        if (width <= 0 || height <= 0 || bgra.Length < (long)width * height * 4)
            throw new ArgumentException("像素数据与尺寸不符", nameof(bgra));

        using var output = new MemoryStream();
        output.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8;  // 位深
        header[9] = 2;  // RGB
        WriteChunk(output, "IHDR", header);

        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                var row = new byte[1 + width * 3];
                row[0] = 1; // Sub 滤波：屏幕内容大片同色，压缩率明显高于不滤波。
                for (var y = 0; y < height; y++)
                {
                    var src = y * width * 4;
                    for (var x = 0; x < width; x++)
                    {
                        var s = src + x * 4;
                        var d = 1 + x * 3;
                        byte r = bgra[s + 2], g = bgra[s + 1], b = bgra[s];
                        if (x == 0)
                        {
                            row[d] = r; row[d + 1] = g; row[d + 2] = b;
                        }
                        else
                        {
                            var p = s - 4;
                            row[d] = (byte)(r - bgra[p + 2]);
                            row[d + 1] = (byte)(g - bgra[p + 1]);
                            row[d + 2] = (byte)(b - bgra[p]);
                        }
                    }
                    zlib.Write(row);
                }
            }
            WriteChunk(output, "IDAT", compressed.ToArray());
        }
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, data.Length);
        output.Write(buffer);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        output.Write(typeBytes);
        output.Write(data);
        var crc = UpdateCrc(UpdateCrc(0xFFFFFFFFu, typeBytes), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buffer, crc);
        output.Write(buffer);
    }

    private static uint UpdateCrc(uint crc, byte[] data)
    {
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }
}

/// <summary>GDI 截屏：按物理像素截取虚拟桌面（全部显示器），缩小时用 HALFTONE 插值。</summary>
[SupportedOSPlatform("windows")]
internal sealed class GdiScreenGrabber : IScreenGrabber
{
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;
    private const int Halftone = 4;
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    public CapturedScreen Capture(int maxWidth)
    {
        // 截屏时临时切到每显示器 DPI 感知，按物理像素取整个桌面，避免高 DPI 下只截到缩放后的一部分。
        var previousContext = TrySetDpiAwareness(PerMonitorAwareV2);
        try
        {
            var left = GetSystemMetrics(SmXVirtualScreen);
            var top = GetSystemMetrics(SmYVirtualScreen);
            var width = GetSystemMetrics(SmCxVirtualScreen);
            var height = GetSystemMetrics(SmCyVirtualScreen);
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException("未检测到可截取的显示器");

            var (targetWidth, targetHeight) = ScaledSize(width, height, maxWidth);
            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new InvalidOperationException("无法访问屏幕（可能处于锁屏或安全桌面）");
            var memoryDc = IntPtr.Zero;
            var bitmap = IntPtr.Zero;
            try
            {
                memoryDc = CreateCompatibleDC(screenDc);
                bitmap = CreateCompatibleBitmap(screenDc, targetWidth, targetHeight);
                if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero)
                    throw new InvalidOperationException("截图内存不足");
                var previous = SelectObject(memoryDc, bitmap);
                bool copied;
                if (targetWidth == width && targetHeight == height)
                {
                    copied = BitBlt(memoryDc, 0, 0, width, height, screenDc, left, top, SrcCopy | CaptureBlt);
                }
                else
                {
                    SetStretchBltMode(memoryDc, Halftone);
                    SetBrushOrgEx(memoryDc, 0, 0, IntPtr.Zero);
                    copied = StretchBlt(memoryDc, 0, 0, targetWidth, targetHeight, screenDc, left, top, width, height, SrcCopy | CaptureBlt);
                }
                SelectObject(memoryDc, previous);
                if (!copied) throw new InvalidOperationException("无法截取屏幕（可能处于锁屏或安全桌面）");

                var info = new BitmapInfoHeader
                {
                    Size = Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = targetWidth,
                    Height = -targetHeight, // 负值表示自上而下。
                    Planes = 1,
                    BitCount = 32,
                };
                var pixels = new byte[targetWidth * targetHeight * 4];
                if (GetDIBits(memoryDc, bitmap, 0, (uint)targetHeight, pixels, ref info, 0) == 0)
                    throw new InvalidOperationException("读取截图像素失败");
                return new CapturedScreen(targetWidth, targetHeight, pixels);
            }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        finally
        {
            if (previousContext != IntPtr.Zero) TrySetDpiAwareness(previousContext);
        }
    }

    internal static (int Width, int Height) ScaledSize(int width, int height, int maxWidth)
    {
        if (width <= maxWidth) return (width, height);
        return (maxWidth, Math.Max(1, (int)Math.Round(height * (double)maxWidth / width)));
    }

    private static IntPtr TrySetDpiAwareness(IntPtr context)
    {
        try { return SetThreadDpiAwarenessContext(context); }
        catch (EntryPointNotFoundException) { return IntPtr.Zero; } // Windows 10 1607 之前没有该接口。
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StretchBlt(IntPtr dest, int x, int y, int width, int height,
        IntPtr src, int srcX, int srcY, int srcWidth, int srcHeight, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr previous);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] bits, ref BitmapInfoHeader info, uint usage);
}
