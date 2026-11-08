namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

/// <summary>
/// 生成菜单用的纯色/圆角纹理。文字走 <see cref="GdiTextRasterizer"/>，这里只画面板。
///
/// 圆角用有符号距离场 + 4x4 超采样，避免 GDI 圆角锯齿；面板尺寸按菜单实际高度生成，
/// 不做九宫格拉伸，保证边角在任何行数下都不变形。
/// </summary>
public static class MenuTextureFactory
{
    private const int SuperSample = 4;

    /// <summary>深色半透明面板 + 细蓝边，与截图里的风格同构。</summary>
    public static RasterSurface CreatePanel(int width, int height, int radius = 18)
        => CreateRoundedRect(
            width, height, radius,
            fillR: 18, fillG: 22, fillB: 30, fillA: 236,
            borderR: 96, borderG: 176, borderB: 224, borderA: 190);

    /// <summary>鼠标悬停行的高亮：白色低透明度。</summary>
    public static RasterSurface CreateHover(int width, int height, int radius = 10)
        => CreateRoundedRect(
            width, height, radius,
            fillR: 255, fillG: 255, fillB: 255, fillA: 34,
            borderR: 255, borderG: 255, borderB: 255, borderA: 0);

    /// <summary>一行分隔线（1px 高，横向渐变不需要，直接用低透明度纯色）。</summary>
    public static RasterSurface CreateSeparator(int width, int height = 2)
    {
        var surface = new RasterSurface(width, height);
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            surface.SetPixel(x, y, 255, 255, 255, 26);

        return surface;
    }

    /// <summary>纯色块（诊断光标用）。</summary>
    public static RasterSurface CreateSolid(int width, int height, byte r, byte g, byte b, byte a)
    {
        var surface = new RasterSurface(Math.Max(1, width), Math.Max(1, height));
        for (var y = 0; y < surface.Height; y++)
        for (var x = 0; x < surface.Width; x++)
            surface.SetPixel(x, y, b, g, r, a);

        return surface;
    }

    /// <summary>
    /// 单行底条：深蓝纵向渐变 + 青色描边 + 内侧金线。
    /// <paramref name="active"/> 为悬停态：底色更亮、描边更亮。
    /// </summary>
    public static RasterSurface CreateRowBar(int width, int height, bool active)
    {
        var surface = new RasterSurface(Math.Max(1, width), Math.Max(1, height));
        const double radius = 10;
        const double border = 1.6;
        const double innerInset = 3.5;

        var top = active ? (r: 20, g: 58, b: 90) : (r: 10, g: 28, b: 46);
        var bottom = active ? (r: 8, g: 26, b: 44) : (r: 5, g: 14, b: 26);
        var fillAlpha = active ? 240 : 225;
        var borderColor = active ? (r: 140, g: 235, b: 255) : (r: 90, g: 210, b: 250);

        for (var y = 0; y < surface.Height; y++)
        {
            var t = surface.Height <= 1 ? 0 : y / (double)(surface.Height - 1);
            var r = (byte)(top.r + (bottom.r - top.r) * t);
            var g = (byte)(top.g + (bottom.g - top.g) * t);
            var b = (byte)(top.b + (bottom.b - top.b) * t);

            for (var x = 0; x < surface.Width; x++)
            {
                var distance = SignedDistanceToRoundedRect(
                    x + 0.5 - surface.Width / 2.0, y + 0.5 - surface.Height / 2.0,
                    surface.Width / 2.0, surface.Height / 2.0, radius);

                if (distance > 0)
                    continue;

                surface.SetPixel(x, y, b, g, r, (byte)fillAlpha);

                if (distance > -border)
                    surface.SetPixel(x, y, (byte)borderColor.b, (byte)borderColor.g, (byte)borderColor.r, 215);

                // 内侧金线（截图里行框有一圈暖色内边）
                if (distance > -innerInset && distance < -innerInset + 1.0)
                    surface.SetPixel(x, y, 80, 170, 210, 150);
            }
        }

        return surface;
    }

    /// <summary>顶部页签：深蓝底 + 底部青色线；选中态更亮。</summary>
    public static RasterSurface CreateTab(int width, int height, bool active)
    {
        var surface = new RasterSurface(Math.Max(1, width), Math.Max(1, height));
        const double radius = 6;

        for (var y = 0; y < surface.Height; y++)
        {
            for (var x = 0; x < surface.Width; x++)
            {
                var distance = SignedDistanceToRoundedRect(
                    x + 0.5 - surface.Width / 2.0, y + 0.5 - surface.Height / 2.0,
                    surface.Width / 2.0, surface.Height / 2.0, radius);

                if (distance > 0)
                    continue;

                if (active)
                    surface.SetPixel(x, y, 128, 92, 40, 235);   // RGB(40,92,128)
                else
                    surface.SetPixel(x, y, 38, 22, 8, 205);     // RGB(8,22,38)

                if (distance > -1.4)
                    surface.SetPixel(x, y, 250, 210, 90, 190);  // RGB(90,210,250)

                // 选中页签底部亮线
                if (active && y >= surface.Height - 3)
                    surface.SetPixel(x, y, 255, 240, 160, 230); // RGB(160,240,255)
            }
        }

        return surface;
    }

    /// <summary>左侧竖栏：半透明深色圆角条。</summary>
    public static RasterSurface CreateSidebar(int width, int height)
    {
        var surface = new RasterSurface(Math.Max(1, width), Math.Max(1, height));
        const double radius = 12;

        for (var y = 0; y < surface.Height; y++)
        {
            for (var x = 0; x < surface.Width; x++)
            {
                var distance = SignedDistanceToRoundedRect(
                    x + 0.5 - surface.Width / 2.0, y + 0.5 - surface.Height / 2.0,
                    surface.Width / 2.0, surface.Height / 2.0, radius);

                if (distance > 0)
                    continue;

                surface.SetPixel(x, y, 26, 12, 8, 210);

                if (distance > -1.2)
                    surface.SetPixel(x, y, 120, 80, 60, 170);
            }
        }

        return surface;
    }

    /// <summary>标题下划线：中间亮、两端渐隐的青色线。</summary>
    public static RasterSurface CreateUnderline(int width, int height)
    {
        var surface = new RasterSurface(Math.Max(1, width), Math.Max(1, height));

        for (var x = 0; x < surface.Width; x++)
        {
            var t = surface.Width <= 1 ? 0 : x / (double)(surface.Width - 1);
            var fade = (byte)(255 * Math.Min(1, Math.Min(t, 1 - t) * 6));
            for (var y = 0; y < surface.Height; y++)
                surface.SetPixel(x, y, 255, 220, 90, fade);
        }

        return surface;
    }

    private static RasterSurface CreateRoundedRect(
        int width,
        int height,
        int radius,
        byte fillR, byte fillG, byte fillB, byte fillA,
        byte borderR, byte borderG, byte borderB, byte borderA)
    {
        var surface = new RasterSurface(Math.Max(1, width), Math.Max(1, height));
        const double borderWidth = 1.6;

        var r = Math.Min(radius, Math.Min(width, height) / 2.0);
        var halfW = width / 2.0;
        var halfH = height / 2.0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var inside = 0;
                var inBorder = 0;

                // 4x4 超采样：只在像素中心点取值会有锯齿
                for (var sy = 0; sy < SuperSample; sy++)
                for (var sx = 0; sx < SuperSample; sx++)
                {
                    var px = x + (sx + 0.5) / SuperSample - halfW;
                    var py = y + (sy + 0.5) / SuperSample - halfH;
                    var distance = SignedDistanceToRoundedRect(px, py, halfW, halfH, r);

                    if (distance <= 0)
                        inside++;
                    if (distance > -borderWidth)
                        inBorder++;
                }

                var insideCoverage = inside / (double)(SuperSample * SuperSample);
                var borderCoverage = inBorder / (double)(SuperSample * SuperSample);

                if (insideCoverage <= 0)
                    continue;

                if (borderA > 0 && borderCoverage > 0)
                {
                    // 先铺边框，再用填充覆盖内部
                    Blend(surface, x, y, borderR, borderG, borderB, (byte)(borderA * borderCoverage));
                }

                Blend(surface, x, y, fillR, fillG, fillB, (byte)(fillA * insideCoverage));
            }
        }

        return surface;
    }

    private static double SignedDistanceToRoundedRect(double px, double py, double halfW, double halfH, double r)
    {
        var dx = Math.Abs(px) - (halfW - r);
        var dy = Math.Abs(py) - (halfH - r);
        var outside = Math.Sqrt(Math.Max(dx, 0) * Math.Max(dx, 0) + Math.Max(dy, 0) * Math.Max(dy, 0));
        var inside = Math.Min(Math.Max(dx, dy), 0);
        return outside + inside - r;
    }

    /// <summary>alpha 混合到目标表面（非预乘）。</summary>
    private static void Blend(RasterSurface surface, int x, int y, byte r, byte g, byte b, byte a)
    {
        if (a == 0)
            return;

        var index = (y * surface.Width + x) * 4;
        var dstA = surface.Pixels[index + 3];

        if (dstA == 0)
        {
            surface.Pixels[index] = b;
            surface.Pixels[index + 1] = g;
            surface.Pixels[index + 2] = r;
            surface.Pixels[index + 3] = a;
            return;
        }

        var src = a / 255.0;
        var dst = dstA / 255.0 * (1 - src);

        var outA = src + dst;
        if (outA <= 0)
            return;

        surface.Pixels[index] = (byte)((b * src + surface.Pixels[index] * dst) / outA);
        surface.Pixels[index + 1] = (byte)((g * src + surface.Pixels[index + 1] * dst) / outA);
        surface.Pixels[index + 2] = (byte)((r * src + surface.Pixels[index + 2] * dst) / outA);
        surface.Pixels[index + 3] = (byte)(outA * 255);
    }
}
