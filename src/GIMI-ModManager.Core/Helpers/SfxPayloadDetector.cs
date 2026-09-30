namespace GIMI_ModManager.Core.Helpers;

/// <summary>追加在 PE 存根之后的自解压包（SFX）载荷类型。</summary>
public enum SfxPayloadKind
{
    /// <summary>7z 载荷（7-Zip 官方 sfx 模块、7-Zip 系安装器）。</summary>
    SevenZip,

    /// <summary>RAR5 载荷 —— WinRAR 自解压包，本功能要支持的主力形态。</summary>
    Rar5,

    /// <summary>RAR4 载荷（老版 WinRAR 存根）。</summary>
    Rar4,

    /// <summary>ZIP 载荷。</summary>
    Zip
}

/// <summary>探测结果。<see cref="PayloadOffset"/> 是载荷签名的起始偏移（相对文件开头）。</summary>
public sealed record SfxPayloadInfo(SfxPayloadKind Kind, long PayloadOffset);

/// <summary>
/// 判断一个 PE 文件是不是「存根 + 追加归档载荷」的自解压包，并给出载荷类型与偏移。
///
/// <para>
/// **为什么不问 7-Zip，而是自己读文件头**：实测 7-Zip 24.09 对**普通 PE** 也会「成功」——
/// <c>7z l -ba 7z.exe</c> 列出的是 PE 各个节（<c>.text</c>/<c>.rdata</c>…，exit 0），
/// <c>7z x 7z.exe</c> 甚至 exit 0 并抽出 7 个「文件」。所以「7z 能不能打开」**不能**当作
/// 「这是个压缩包」的判据：那样任何 exe（安装器、游戏主程序）都会被当成 Mod 包解压出来。
/// 这里改成看**文件结构**：只有 PE 镜像之后真的追加了一个归档（有归档魔数），才算自解压包。
/// 判据纯字节、不启进程、只读几 KB。
/// </para>
///
/// <para>
/// 已知边界：本探测只认「PE + 追加归档」这一种形态（WinRAR SFX 是典型；用户实测的样本
/// 99.4% 的字节都是追加的 RAR5 载荷）。NSIS / Inno 这类**自制格式**的安装器即便 7z 读得出，
/// 也不会有归档魔数 —— 它们不会走进这条链路。
/// </para>
/// </summary>
public static class SfxPayloadDetector
{
    /// <summary>默认在 overlay 起始处往后搜多远。载荷通常紧贴 overlay 开头（用户样本 99.4% 的字节都在那里）。</summary>
    public const int DefaultSearchWindowBytes = 1024 * 1024;

    /// <summary>PE 节表里每节 40 字节；节数上限取一个宽松但足以挡掉畸形文件的数字。</summary>
    private const int MaxSectionCount = 96;

    private const int E_lfanewOffset = 0x3C;
    private const int SectionHeaderSize = 40;
    private const int SizeOfRawDataOffset = 16;
    private const int PointerToRawDataOffset = 20;

    // 签名按「长的在前」排，好在同一偏移同时命中时优先取更具体的那个
    // （RAR4 的 7 字节是 RAR5 前 7 字节的前缀）。
    private static readonly (SfxPayloadKind Kind, byte[] Signature)[] Signatures =
    {
        (SfxPayloadKind.Rar5, new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 }),
        (SfxPayloadKind.Rar4, new byte[] { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 }),
        (SfxPayloadKind.SevenZip, new byte[] { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C }),
        (SfxPayloadKind.Zip, new byte[] { 0x50, 0x4B, 0x03, 0x04 })
    };

    /// <summary>
    /// 探测磁盘上的文件。不是「PE + 追加归档」一律返回 <c>null</c>（含文件不存在、无权限、被占用）。
    /// **绝不抛异常** —— 调用方拿它当「要不要走自解压这条路」的闸门，一个读不了的文件只该是「不走」。
    /// </summary>
    public static SfxPayloadInfo? TryDetect(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return TryDetect(stream);
        }
        catch (Exception)
        {
            // IOException / UnauthorizedAccessException / 路径非法……对探测而言都是「不是自解压包」
            return null;
        }
    }

    /// <summary>探测流（需可定位）。同样绝不抛异常。</summary>
    public static SfxPayloadInfo? TryDetect(Stream? stream, int searchWindowBytes = DefaultSearchWindowBytes)
    {
        if (stream is null || !stream.CanSeek || !stream.CanRead)
            return null;

        try
        {
            var payloadOffset = TryGetOverlayStart(stream);
            if (payloadOffset is null)
                return null;

            var remaining = stream.Length - payloadOffset.Value;
            if (remaining <= 0)
                return null;

            var windowLength = (int)Math.Min(searchWindowBytes, remaining);
            var window = new byte[windowLength];

            stream.Position = payloadOffset.Value;
            var read = 0;
            while (read < windowLength)
            {
                var chunk = stream.Read(window, read, windowLength - read);
                if (chunk <= 0)
                    break;

                read += chunk;
            }

            if (read <= 0)
                return null;

            var found = FindFirstSignature(window, read);
            return found is null
                ? null
                : new SfxPayloadInfo(found.Value.Kind, payloadOffset.Value + found.Value.Index);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 算出 PE overlay 的起始偏移：所有节的 <c>PointerToRawData + SizeOfRawData</c> 的最大值。
    /// 不是合法 PE（MZ 开头 / e_lfanew 指向 PE 签名 / 节表合理）则返回 <c>null</c>。
    /// </summary>
    private static long? TryGetOverlayStart(Stream stream)
    {
        if (stream.Length < E_lfanewOffset + 4 + 4 + 20)
            return null;

        var header = new byte[4];
        if (!ReadAt(stream, 0, header, 2) || header[0] != 0x4D || header[1] != 0x5A) // 'MZ'
            return null;

        var lfanewBytes = new byte[4];
        if (!ReadAt(stream, E_lfanewOffset, lfanewBytes, 4))
            return null;

        long peOffset = BitConverter.ToInt32(lfanewBytes, 0);
        if (peOffset <= 0 || peOffset + 24 > stream.Length)
            return null;

        var peHeader = new byte[24];
        if (!ReadAt(stream, peOffset, peHeader, 24))
            return null;

        if (peHeader[0] != 0x50 || peHeader[1] != 0x45 || peHeader[2] != 0 || peHeader[3] != 0) // 'PE\0\0'
            return null;

        var numberOfSections = BitConverter.ToUInt16(peHeader, 6);
        var sizeOfOptionalHeader = BitConverter.ToUInt16(peHeader, 20);
        if (numberOfSections == 0 || numberOfSections > MaxSectionCount)
            return null;

        long sectionTableOffset = peOffset + 24 + sizeOfOptionalHeader;
        var sectionTableLength = numberOfSections * SectionHeaderSize;
        if (sectionTableOffset + sectionTableLength > stream.Length)
            return null;

        var sectionTable = new byte[sectionTableLength];
        if (!ReadAt(stream, sectionTableOffset, sectionTable, sectionTableLength))
            return null;

        long overlayStart = 0;
        for (var i = 0; i < numberOfSections; i++)
        {
            var entry = i * SectionHeaderSize;
            var sizeOfRawData = BitConverter.ToUInt32(sectionTable, entry + SizeOfRawDataOffset);
            var pointerToRawData = BitConverter.ToUInt32(sectionTable, entry + PointerToRawDataOffset);

            // 全部为 0 的节（未初始化数据）不占文件空间，跳过
            if (sizeOfRawData == 0 || pointerToRawData == 0)
                continue;

            var end = (long)pointerToRawData + sizeOfRawData;
            if (end > overlayStart)
                overlayStart = end;
        }

        if (overlayStart <= 0 || overlayStart > stream.Length)
            return null;

        return overlayStart;
    }

    /// <summary>在缓冲区内找最早的归档签名；同一偏移命中多个时取签名更长的那个（RAR5 压过 RAR4）。</summary>
    private static (SfxPayloadKind Kind, int Index)? FindFirstSignature(byte[] buffer, int length)
    {
        var span = buffer.AsSpan(0, length);
        var bestIndex = -1;
        var bestKind = default(SfxPayloadKind);
        var bestLength = 0;

        foreach (var (kind, signature) in Signatures)
        {
            var index = span.IndexOf(signature);
            if (index < 0)
                continue;

            if (bestIndex < 0 || index < bestIndex || (index == bestIndex && signature.Length > bestLength))
            {
                bestIndex = index;
                bestKind = kind;
                bestLength = signature.Length;
            }
        }

        return bestIndex < 0 ? null : (bestKind, bestIndex);
    }

    private static bool ReadAt(Stream stream, long offset, byte[] buffer, int count)
    {
        stream.Position = offset;
        var read = 0;
        while (read < count)
        {
            var chunk = stream.Read(buffer, read, count - read);
            if (chunk <= 0)
                return false;

            read += chunk;
        }

        return true;
    }
}
