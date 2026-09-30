using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="SfxPayloadDetector"/> —— 也就是「这个 exe 到底要不要按压缩包处理」那道闸门。
///
/// 这道闸门判错的两种后果都很糟，所以两个方向都要钉死：
/// <list type="bullet">
/// <item>把普通程序判成自解压包 → 7-Zip 会把 PE 的**节**当文件抽出来（实测 <c>7z x 7z.exe</c> exit 0
/// 且抽出 7 个文件），用户会在安装向导里看到一堆 <c>.text</c>/<c>.rdata</c>；大安装器还要白解压几百 MB。</item>
/// <item>把真自解压包判成普通程序 → 功能直接不可用（而这类包恰恰是本功能存在的理由）。</item>
/// </list>
/// 测试用**手工合成的 PE**（自己拼文件头），不依赖任何真实样本，所以能进版本库、也不受样本更新影响。
/// </summary>
public class SfxPayloadDetectorTests
{
    // 合成 PE 的固定布局：这些值只要求自洽，不要求像真 PE
    private const int PeHeaderOffset = 0x80; // e_lfanew 指向这里
    private const int CoffHeaderOffset = PeHeaderOffset + 4; // 「PE\0\0」之后
    private const ushort SizeOfOptionalHeader = 0xE0;
    private const int SectionTableOffset = CoffHeaderOffset + 20 + SizeOfOptionalHeader; // 0x178
    private const uint SizeOfRawData = 0x200;
    private const uint PointerToRawData = 0x400;
    private const int OverlayStart = (int)(PointerToRawData + SizeOfRawData); // 0x600

    private const int SizeOfRawDataFieldOffset = 16;
    private const int PointerToRawDataFieldOffset = 20;

    private static readonly byte[] Rar5Signature = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };
    private static readonly byte[] Rar4Signature = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 };
    private static readonly byte[] SevenZipSignature = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };
    private static readonly byte[] ZipSignature = { 0x50, 0x4B, 0x03, 0x04 };

    [Fact]
    public void ARar5PayloadIsRar5()
    {
        var file = BuildPe(Rar5Signature);

        var result = SfxPayloadDetector.TryDetect(new MemoryStream(file));

        Assert.NotNull(result);
        Assert.Equal(SfxPayloadKind.Rar5, result!.Kind);
        Assert.Equal(OverlayStart, result.PayloadOffset);
    }

    [Fact]
    public void ARar4PayloadIsRar4()
    {
        var result = SfxPayloadDetector.TryDetect(new MemoryStream(BuildPe(Rar4Signature)));

        Assert.NotNull(result);
        Assert.Equal(SfxPayloadKind.Rar4, result!.Kind);
    }

    [Fact]
    public void Rar5IsNotMisreportedAsRar4()
    {
        // RAR4 的 7 字节签名是 RAR5 前 7 字节的前缀：同一偏移两者都能匹配，
        // 必须取签名更长的那个，否则 7z 之外的代码会按 RAR4 去理解 RAR5 头
        var result = SfxPayloadDetector.TryDetect(new MemoryStream(BuildPe(Rar5Signature)));

        Assert.NotNull(result);
        Assert.NotEqual(SfxPayloadKind.Rar4, result!.Kind);
    }

    [Fact]
    public void ASevenZipPayloadIsSevenZip()
    {
        var result = SfxPayloadDetector.TryDetect(new MemoryStream(BuildPe(SevenZipSignature)));

        Assert.NotNull(result);
        Assert.Equal(SfxPayloadKind.SevenZip, result!.Kind);
    }

    [Fact]
    public void AZipPayloadIsZip()
    {
        var result = SfxPayloadDetector.TryDetect(new MemoryStream(BuildPe(ZipSignature)));

        Assert.NotNull(result);
        Assert.Equal(SfxPayloadKind.Zip, result!.Kind);
    }

    [Fact]
    public void APayloadAfterAStubConfigBlockIsStillFound()
    {
        // 7z 官方 sfx 模块的布局是「存根 + config.txt + 载荷.7z」：载荷并不紧贴 overlay 开头。
        // 用户的 WinRAR 样本恰好是紧贴的，但判据不能只会看第一个字节
        var result = SfxPayloadDetector.TryDetect(new MemoryStream(BuildPe(SevenZipSignature, gapBytes: 512)));

        Assert.NotNull(result);
        Assert.Equal(SfxPayloadKind.SevenZip, result!.Kind);
        Assert.Equal(OverlayStart + 512, result.PayloadOffset);
    }

    [Fact]
    public void APayloadBeyondTheSearchWindowIsGivenUpOn()
    {
        // 搜索窗口有上限（默认 1 MB）：极大偏移的载荷不是我们要支持的形态，宁可判「不是」也不去扫整个文件
        var file = BuildPe(SevenZipSignature, gapBytes: 100);

        Assert.Null(SfxPayloadDetector.TryDetect(new MemoryStream(file), searchWindowBytes: 50));
        Assert.NotNull(SfxPayloadDetector.TryDetect(new MemoryStream(file), searchWindowBytes: 200));
    }

    [Fact]
    public void APeWithNoAppendedDataIsNotAnSfx()
    {
        // overlay 起点就是文件末尾 —— 普通 PE，没有追加任何东西
        Assert.Null(SfxPayloadDetector.TryDetect(new MemoryStream(BuildPe())));
    }

    [Fact]
    public void APeWithNonArchiveAppendedDataIsNotAnSfx()
    {
        // 追加了东西但不是归档（自制安装器的配置块之类）—— 这才是「不能一见到 exe 就解压」的关键一条
        var junk = new byte[64];
        junk[0] = 0x09;
        junk[1] = 0x48;

        Assert.Null(SfxPayloadDetector.TryDetect(new MemoryStream(BuildPe(junk))));
    }

    [Fact]
    public void APlainTextFileIsNotAnSfx()
    {
        var text = System.Text.Encoding.ASCII.GetBytes("just a text file, not an executable at all");

        Assert.Null(SfxPayloadDetector.TryDetect(new MemoryStream(text)));
    }

    [Fact]
    public void ARandomBinaryWithMzPrefixButNoPeHeaderIsNotAnSfx()
    {
        var bytes = new byte[0x200];
        bytes[0] = 0x4D;
        bytes[1] = 0x5A;
        BitConverter.GetBytes(0x40).CopyTo(bytes, 0x3C); // e_lfanew 指向一段垃圾
        bytes[0x40] = 0x11;

        Assert.Null(SfxPayloadDetector.TryDetect(new MemoryStream(bytes)));
    }

    [Fact]
    public void AMalformedPeWithSectionsBeyondEofIsNotAnSfx()
    {
        var file = BuildPe();
        WriteUInt32(file, SectionTableOffset + SizeOfRawDataFieldOffset, 0xFFFFFF);

        Assert.Null(SfxPayloadDetector.TryDetect(new MemoryStream(file)));
    }

    [Fact]
    public void AnEmptyOrUnseekableStreamIsNotAnSfx()
    {
        Assert.Null(SfxPayloadDetector.TryDetect(Stream.Null));
        Assert.Null(SfxPayloadDetector.TryDetect(new MemoryStream(Array.Empty<byte>())));
        Assert.Null(SfxPayloadDetector.TryDetect(null));
    }

    [Fact]
    public void AMissingFileIsNotAnSfx()
    {
        // 探测是「要不要走自解压这条路」的闸门：读不了就只是「不走」，不能把启动/拖拽路径搞崩
        var missing = Path.Combine(Path.GetTempPath(), $"jasm-sfx-missing-{Guid.NewGuid():N}.exe");

        Assert.Null(SfxPayloadDetector.TryDetect(missing));
        Assert.Null(SfxPayloadDetector.TryDetect(null));
        Assert.Null(SfxPayloadDetector.TryDetect("   "));
    }

    [Fact]
    public void DetectionWorksFromARealFilePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jasm-sfx-detect-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(path, BuildPe(Rar5Signature));

            var result = SfxPayloadDetector.TryDetect(path);

            Assert.NotNull(result);
            Assert.Equal(SfxPayloadKind.Rar5, result!.Kind);
            Assert.Equal(OverlayStart, result.PayloadOffset);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>手工拼一个最小可解析的 PE：MZ + e_lfanew + PE 签名 + COFF 头 + 一个节的节表。</summary>
    private static byte[] BuildPe(byte[]? overlay = null, int gapBytes = 0)
    {
        var payloadLength = overlay?.Length ?? 0;
        var buffer = new byte[OverlayStart + gapBytes + payloadLength];

        buffer[0] = 0x4D; // 'M'
        buffer[1] = 0x5A; // 'Z'
        BitConverter.GetBytes(PeHeaderOffset).CopyTo(buffer, 0x3C);

        buffer[PeHeaderOffset] = 0x50; // 'P'
        buffer[PeHeaderOffset + 1] = 0x45; // 'E'

        WriteUInt16(buffer, CoffHeaderOffset + 2, 1); // NumberOfSections
        WriteUInt16(buffer, CoffHeaderOffset + 16, SizeOfOptionalHeader);
        WriteUInt32(buffer, SectionTableOffset + SizeOfRawDataFieldOffset, SizeOfRawData);
        WriteUInt32(buffer, SectionTableOffset + PointerToRawDataFieldOffset, PointerToRawData);

        overlay?.CopyTo(buffer, OverlayStart + gapBytes);

        return buffer;
    }

    private static void WriteUInt16(byte[] buffer, int offset, ushort value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static void WriteUInt32(byte[] buffer, int offset, uint value) =>
        BitConverter.GetBytes(value).CopyTo(buffer, offset);
}
