using System.Buffers.Binary;

namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

/// <summary>
/// 写 3DMigoto 能直接加载的未压缩 DDS（BGRA8）。
///
/// 不用 BCn 压缩：菜单图是文字与纯色面板，未压缩画质更好；而这些图很小，
/// 体积不是问题。DXT 编码器还要额外依赖，违反本仓库「不新增依赖」的约定。
/// </summary>
public static class DdsWriter
{
    private const uint DdsMagic = 0x20534444; // "DDS "
    private const uint DdsdCaps = 0x1;
    private const uint DdsdHeight = 0x2;
    private const uint DdsdWidth = 0x4;
    private const uint DdsdPitch = 0x8;
    private const uint DdsdPixelFormat = 0x1000;
    private const uint DdpfAlphaPixels = 0x1;
    private const uint DdpfRgb = 0x40;
    private const uint DdscapsTexture = 0x1000;

    private const int HeaderSize = 128;

    /// <summary>
    /// 生成 DDS 字节。像素必须是 BGRA、自上而下、行宽 = width * 4。
    /// </summary>
    public static byte[] CreateBgra32(int width, int height, ReadOnlySpan<byte> bgraPixels)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "DDS 尺寸必须为正数");

        var expected = checked(width * height * 4);
        if (bgraPixels.Length != expected)
            throw new ArgumentException($"像素长度应为 {expected}，实际 {bgraPixels.Length}", nameof(bgraPixels));

        var dds = new byte[HeaderSize + expected];
        var span = dds.AsSpan();

        BinaryPrimitives.WriteUInt32LittleEndian(span[0..], DdsMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], 124); // dwSize
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..],
            DdsdCaps | DdsdHeight | DdsdWidth | DdsdPitch | DdsdPixelFormat);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)(width * 4)); // dwPitchOrLinearSize
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], 0);                 // dwDepth
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], 0);                 // dwMipMapCount
        // 32..75 为 dwReserved1[11]，保持 0

        var pf = span[76..108];
        BinaryPrimitives.WriteUInt32LittleEndian(pf[0..], 32);                   // ddspf.dwSize
        BinaryPrimitives.WriteUInt32LittleEndian(pf[4..], DdpfRgb | DdpfAlphaPixels);
        BinaryPrimitives.WriteUInt32LittleEndian(pf[8..], 0);                    // dwFourCC
        BinaryPrimitives.WriteUInt32LittleEndian(pf[12..], 32);                  // dwRGBBitCount
        BinaryPrimitives.WriteUInt32LittleEndian(pf[16..], 0x00FF0000);          // R
        BinaryPrimitives.WriteUInt32LittleEndian(pf[20..], 0x0000FF00);          // G
        BinaryPrimitives.WriteUInt32LittleEndian(pf[24..], 0x000000FF);          // B
        BinaryPrimitives.WriteUInt32LittleEndian(pf[28..], 0xFF000000);          // A

        BinaryPrimitives.WriteUInt32LittleEndian(span[108..], DdscapsTexture);   // dwCaps
        // 112..127 全 0

        bgraPixels.CopyTo(span[HeaderSize..]);
        return dds;
    }
}
