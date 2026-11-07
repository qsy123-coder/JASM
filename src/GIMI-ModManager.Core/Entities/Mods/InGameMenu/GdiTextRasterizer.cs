using System.Runtime.InteropServices;

namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

/// <summary>一块 BGRA8、自上而下的位图，供 <see cref="DdsWriter"/> 直接落盘。</summary>
public sealed class RasterSurface(int width, int height)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = new byte[width * height * 4];

    /// <summary>写一个像素（BGRA 顺序，非预乘 alpha）。</summary>
    public void SetPixel(int x, int y, byte b, byte g, byte r, byte a)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
            return;

        var index = (y * Width + x) * 4;
        Pixels[index] = b;
        Pixels[index + 1] = g;
        Pixels[index + 2] = r;
        Pixels[index + 3] = a;
    }
}

/// <summary>
/// 用 GDI 把文字画成纹理。GDI 不写 alpha 通道，所以画完后把亮度当覆盖率转成 alpha，
/// RGB 统一置白——配合 3DMigoto 的 <c>ADD SRC_ALPHA INV_SRC_ALPHA</c> 就是标准的抗锯齿文字。
///
/// 选择 GDI 而不是 Win2D / System.Drawing：本仓库没有 Win2D，也不想为一个文字位图引入
/// 新依赖（CLAUDE.md 明确禁止）；GDI32 是系统自带，P/Invoke 即可。
/// </summary>
public static class GdiTextRasterizer
{
    private const uint DtLeft = 0x0000;
    private const uint DtCenter = 0x0001;
    private const uint DtRight = 0x0002;
    private const uint DtVCenter = 0x0004;
    private const uint DtSingleLine = 0x0020;
    private const uint DtNoPrefix = 0x0800;
    private const uint DtEndEllipsis = 0x8000;

    private const int DibRgbColors = 0;
    private const uint BiRgb = 0;
    private const int Transparent = 1;

    public enum TextAlign
    {
        Left,
        Center,
        Right,
    }

    /// <summary>
    /// 在固定尺寸的画布上画一行文字。超出宽度时用省略号截断（DT_END_ELLIPSIS），
    /// 不做自动换行/缩小字号，避免菜单行高不齐。
    /// </summary>
    public static RasterSurface RenderText(
        string text,
        int width,
        int height,
        int fontSize,
        bool bold,
        TextAlign align)
    {
        var surface = new RasterSurface(Math.Max(1, width), Math.Max(1, height));

        if (string.IsNullOrEmpty(text))
            return surface;

        var hdcScreen = GetDC(IntPtr.Zero);
        if (hdcScreen == IntPtr.Zero)
            return surface;

        var hdc = CreateCompatibleDC(hdcScreen);
        var dib = IntPtr.Zero;
        var font = IntPtr.Zero;
        var previousBitmap = IntPtr.Zero;
        var previousFont = IntPtr.Zero;

        try
        {
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = surface.Width,
                    Height = -surface.Height, // 负高度 = 自上而下
                    Planes = 1,
                    BitCount = 32,
                    Compression = BiRgb,
                    SizeImage = (uint)(surface.Width * surface.Height * 4),
                },
            };

            dib = CreateDIBSection(hdc, ref info, DibRgbColors, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                return surface;

            previousBitmap = SelectObject(hdc, dib);

            font = CreateFontW(
                -fontSize, 0, 0, 0,
                bold ? 600 : 400,
                0, 0, 0,
                1,   // DEFAULT_CHARSET
                4,   // OUT_TT_PRECIS
                0,   // CLIP_DEFAULT_PRECIS
                4,   // ANTIALIASED_QUALITY（灰度抗锯齿，避免 ClearType 彩边）
                0,
                "Microsoft YaHei UI");

            if (font != IntPtr.Zero)
                previousFont = SelectObject(hdc, font);

            SetBkMode(hdc, Transparent);
            SetTextColor(hdc, unchecked((int)0x00FFFFFF));

            var rect = new Rect
            {
                Left = 0,
                Top = 0,
                Right = surface.Width,
                Bottom = surface.Height,
            };

            var flags = DtSingleLine | DtVCenter | DtNoPrefix | DtEndEllipsis | align switch
            {
                TextAlign.Center => DtCenter,
                TextAlign.Right => DtRight,
                _ => DtLeft,
            };

            DrawTextW(hdc, text, text.Length, ref rect, flags);
            GdiFlush();

            CopyToSurface(bits, surface);
        }
        finally
        {
            if (previousFont != IntPtr.Zero)
                SelectObject(hdc, previousFont);
            if (previousBitmap != IntPtr.Zero)
                SelectObject(hdc, previousBitmap);
            if (font != IntPtr.Zero)
                DeleteObject(font);
            if (dib != IntPtr.Zero)
                DeleteObject(dib);
            DeleteDC(hdc);
            ReleaseDC(IntPtr.Zero, hdcScreen);
        }

        return surface;
    }

    /// <summary>
    /// DIB 里是 GDI 画的灰度字（RGB 相同、alpha 未写），这里把亮度转成 alpha、RGB 置白。
    /// </summary>
    private static void CopyToSurface(IntPtr bits, RasterSurface surface)
    {
        var byteCount = surface.Width * surface.Height * 4;
        var source = new byte[byteCount];
        Marshal.Copy(bits, source, 0, byteCount);

        for (var i = 0; i < byteCount; i += 4)
        {
            var b = source[i];
            var g = source[i + 1];
            var r = source[i + 2];
            var coverage = Math.Max(r, Math.Max(g, b));

            if (coverage == 0)
                continue;

            surface.Pixels[i] = 0xFF;
            surface.Pixels[i + 1] = 0xFF;
            surface.Pixels[i + 2] = 0xFF;
            surface.Pixels[i + 3] = coverage;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors; // 只用到单色表时的占位；BI_RGB 32bpp 不会读它
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern int DeleteDC(IntPtr hDc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hDc, ref BitmapInfo pbmi, uint usage, out IntPtr bits,
        IntPtr hSection, uint offset);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hDc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hDc, int mode);

    [DllImport("gdi32.dll")]
    private static extern int SetTextColor(IntPtr hDc, int color);

    [DllImport("gdi32.dll")]
    private static extern void GdiFlush();

    [DllImport("gdi32.dll", EntryPoint = "CreateFontW", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(
        int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision,
        uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);

    [DllImport("user32.dll", EntryPoint = "DrawTextW", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hDc, string text, int length, ref Rect rect, uint flags);
}
