using System.Text;

namespace GIMI_ModManager.Core.Entities.Mods.InGameMenu;

/// <summary>
/// 读取 Mod ini 的文本，处理真实 Mod 池里最常见的三种编码：
/// UTF-8（含/不含 BOM）、UTF-16（BOM）与中文 Mod 常见的 GB18030。
///
/// 之所以不直接用 <c>File.ReadAllLines</c>：它默认 UTF-8，遇到 GBK 写的中文注释会变成
/// 替换字符，菜单标签就会乱码，而这恰恰是「注释即标签」方案最依赖的数据。
/// </summary>
public static class IniFileText
{
    private static readonly object EncodingLock = new();
    private static bool _codePagesRegistered;

    public static string[] ReadAllLines(string path)
        => ReadAllText(path).Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

    public static string ReadAllText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return Decode(bytes);
    }

    /// <summary>
    /// 从字节判断编码并解码。判定顺序：BOM → 合法 UTF-8 → GB18030 → Latin-1 兜底。
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
            return string.Empty;

        // UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);

        // UTF-16 LE / BE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);

        // 没有 BOM：先严格试 UTF-8。合法 UTF-8 的字节序列极少和 GBK 撞车，
        // 但反过来 GBK 几乎一定不是合法 UTF-8，所以这个顺序是安全的。
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 落到 GB18030（GBK 的超集）
        }

        var gb18030 = TryGetGb18030();
        if (gb18030 is not null)
            return gb18030.GetString(bytes);

        // 最后的兜底：不抛异常，保证菜单仍能生成，只是标签可能不准
        return Encoding.Latin1.GetString(bytes);
    }

    private static Encoding? TryGetGb18030()
    {
        try
        {
            EnsureCodePagesRegistered();
            return Encoding.GetEncoding(54936); // GB18030
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void EnsureCodePagesRegistered()
    {
        if (_codePagesRegistered)
            return;

        lock (EncodingLock)
        {
            if (_codePagesRegistered)
                return;

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _codePagesRegistered = true;
        }
    }
}
