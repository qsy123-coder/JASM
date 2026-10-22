using System.Runtime.InteropServices;
using GIMI_ModManager.Core.Helpers;
using Serilog;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace GIMI_ModManager.WinUI.Services.Overlay;

/// <summary>
/// 浮窗截图：把屏幕上的一块矩形抓成 BGRA 像素，再按 Mod 名写成 PNG。
///
/// <para>
/// 用 GDI <c>BitBlt</c> 而不是 Windows.Graphics.Capture：后者要为每次截图起 Direct3D 设备与
/// 帧池，代码量与失败面都比这里大。GDI 的已知代价是**独占全屏**下可能抓不到画面（返回全黑），
/// 这一点必须在实机上验（PRD 的 Phase 0），不能当成已经可用。
/// </para>
///
/// <para>
/// 像素缓冲的坐标系是**虚拟屏幕物理像素**（副屏在主屏左边时 X 为负），与
/// <see cref="OverlayCaptureRegion"/> / <see cref="OverlayRect"/> 一致；本类不做任何 DIP 换算。
/// </para>
/// </summary>
public sealed class OverlayScreenshotService
{
    /// <summary>PNG 编码采用 96 DPI 的元数据。截图本身不靠它定位，写死即可。</summary>
    private const double PngDpi = 96;

    private readonly ILogger _logger;

    public OverlayScreenshotService(ILogger logger)
        => _logger = logger.ForContext<OverlayScreenshotService>();

    /// <summary>默认输出目录：<c>图片\JASM\浮窗截图</c>。设置里没配过时用它，保证首次截图能直接落盘。</summary>
    public static string GetDefaultOutputFolder()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "JASM", "浮窗截图");

    /// <summary>整个虚拟屏幕的边界（物理像素）。框选窗口与「复用上次区域」的夹取都以它为准。</summary>
    public static OverlayRect GetVirtualScreenBounds()
        => new(
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));

    /// <summary>
    /// 抓取一块屏幕区域。失败（拿不到屏幕 DC / DIB 段）时抛 <see cref="InvalidOperationException"/>，
    /// 调用方把它变成浮窗上的一句话；这里不吞异常 —— 静默返回一张黑图比报错更糟。
    /// </summary>
    public unsafe CapturedFrame Capture(OverlayCaptureRegion region)
    {
        if (!region.IsUsable)
            throw new ArgumentOutOfRangeException(nameof(region), "截图区域太小。");

        var screenDc = PInvoke.GetDC(HWND.Null);

        if (screenDc.IsNull)
            throw new InvalidOperationException("拿不到屏幕设备上下文（GetDC 失败）。");

        HDC memoryDc = default;
        // CsWin32 把这个句柄生成成 DeleteObjectSafeHandle：Dispose 时自己调 DeleteObject，
        // 所以下面只负责在它之前把位图从 DC 里换出来，不再手写一次 DeleteObject（那会重复释放）。
        DeleteObjectSafeHandle? bitmap = null;
        HGDIOBJ previous = default;
        var selected = false;

        try
        {
            memoryDc = PInvoke.CreateCompatibleDC(screenDc);

            if (memoryDc.IsNull)
                throw new InvalidOperationException("创建内存设备上下文失败。");

            var info = new BITMAPINFO();
            info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = region.Width;
            // 负高度 = 自上而下的 DIB：省掉后面逐行翻转，像素数组的第一行就是屏幕最上面一行
            info.bmiHeader.biHeight = -region.Height;
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;

            bitmap = PInvoke.CreateDIBSection(screenDc, &info, DIB_USAGE.DIB_RGB_COLORS,
                out var bits, null, 0);

            if (bitmap is null || bitmap.IsInvalid || bits is null)
                throw new InvalidOperationException("创建位图失败。");

            previous = PInvoke.SelectObject(memoryDc, new HGDIOBJ(bitmap.DangerousGetHandle()));
            selected = true;

            if (!PInvoke.BitBlt(memoryDc, 0, 0, region.Width, region.Height, screenDc,
                    region.X, region.Y, ROP_CODE.SRCCOPY))
            {
                throw new InvalidOperationException("抓取屏幕像素失败（BitBlt 返回 0）。");
            }

            var pixels = new byte[region.Width * region.Height * 4];
            Marshal.Copy((nint)bits, pixels, 0, pixels.Length);

            _logger.Debug("[浮窗截图] 已抓取 {Width}x{Height} @ ({X},{Y})",
                region.Width, region.Height, region.X, region.Y);

            return new CapturedFrame(region.X, region.Y, region.Width, region.Height, pixels);
        }
        finally
        {
            if (selected)
                PInvoke.SelectObject(memoryDc, previous);

            bitmap?.Dispose();

            if (!memoryDc.IsNull)
                PInvoke.DeleteDC(memoryDc);

            PInvoke.ReleaseDC(HWND.Null, screenDc);
        }
    }

    /// <summary>
    /// 从一张整屏截图里裁出一块区域。框选窗口是「先抓整屏、把整屏显示出来让你框」，
    /// 所以最终落盘的是这里裁出来的结果 —— 它保证截图里不会混进框选遮罩本身。
    /// </summary>
    public CapturedFrame Crop(CapturedFrame frame, OverlayCaptureRegion region)
    {
        var left = region.X - frame.X;
        var top = region.Y - frame.Y;

        if (left < 0 || top < 0 || left + region.Width > frame.Width || top + region.Height > frame.Height)
            throw new ArgumentOutOfRangeException(nameof(region), "截图区域不在整屏截图的范围内。");

        var pixels = new byte[region.Width * region.Height * 4];
        var sourceStride = frame.Width * 4;
        var targetStride = region.Width * 4;

        for (var row = 0; row < region.Height; row++)
        {
            System.Buffer.BlockCopy(frame.Pixels, (top + row) * sourceStride + left * 4,
                pixels, row * targetStride, targetStride);
        }

        return new CapturedFrame(region.X, region.Y, region.Width, region.Height, pixels);
    }

    /// <summary>
    /// 按 Mod 名把一张截图写成 PNG，返回最终完整路径。
    ///
    /// 目录不存在就建；重名自动加 <c>-2</c> / <c>-3</c>（见 <see cref="OverlayScreenshotNaming"/>）。
    /// 调用方负责把显示名交进来，这里统一清洗 —— 清洗规则只有这一份。
    /// </summary>
    public async Task<string> SavePngAsync(CapturedFrame frame, string outputFolder, string displayName,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputFolder);

        var baseName = OverlayScreenshotNaming.SanitizeBaseName(displayName);
        var path = OverlayScreenshotNaming.ResolveUniqueFilePath(outputFolder, baseName, ".png", File.Exists);

        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream)
            .AsTask(cancellationToken).ConfigureAwait(false);

        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)frame.Width, (uint)frame.Height, PngDpi, PngDpi, frame.Pixels);

        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

        stream.Seek(0);

        await using (var file = File.Create(path))
        {
            await stream.AsStreamForRead().CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        _logger.Information("[浮窗截图] 已保存 {Path}（{Width}x{Height}）",
            path, frame.Width, frame.Height);

        return path;
    }
}

/// <summary>
/// 一张已经抓在内存里的截图。坐标是虚拟屏幕物理像素；像素格式固定 BGRA8、自上而下。
///
/// 用记录类而不是 <c>SoftwareBitmap</c>：裁剪只是内存行拷贝，而 <c>SoftwareBitmap</c>
/// 主要服务于 XAML 显示，落盘前没必要多一层 WinRT 对象生命周期。
/// </summary>
public sealed record CapturedFrame(int X, int Y, int Width, int Height, byte[] Pixels);
