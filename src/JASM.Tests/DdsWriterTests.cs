using System.Buffers.Binary;
using GIMI_ModManager.Core.Entities.Mods.InGameMenu;

namespace JASM.Tests;

/// <summary>
/// DDS 头是 3DMigoto 认不认这张图的关键。这里锁住魔数、尺寸、BGRA 掩码与像素负载长度。
/// </summary>
public class DdsWriterTests
{
    [Fact]
    public void WritesLegacyBgra32Header()
    {
        var pixels = new byte[2 * 3 * 4];
        var dds = DdsWriter.CreateBgra32(2, 3, pixels);

        Assert.Equal(128 + pixels.Length, dds.Length);
        Assert.Equal(0x20534444u, BinaryPrimitives.ReadUInt32LittleEndian(dds));
        Assert.Equal(124u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(4)));
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(12))); // height
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(16))); // width
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(20))); // pitch

        // ddspf @ 76
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(76)));
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(88))); // bit count
        Assert.Equal(0x00FF0000u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(92))); // R
        Assert.Equal(0xFF000000u, BinaryPrimitives.ReadUInt32LittleEndian(dds.AsSpan(104))); // A
    }

    [Fact]
    public void CopiesPixelsAfterHeader()
    {
        var pixels = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var dds = DdsWriter.CreateBgra32(1, 2, pixels);

        Assert.Equal(pixels, dds[128..]);
    }

    [Fact]
    public void RejectsMismatchedPixelLength()
    {
        Assert.Throws<ArgumentException>(() => DdsWriter.CreateBgra32(2, 2, new byte[4]));
    }
}
