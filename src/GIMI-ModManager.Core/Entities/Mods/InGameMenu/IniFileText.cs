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
    /// <summary>标记 JASM 自动补进 Mod ini 的 namespace 行，<see cref="TryRemoveAddedNamespace"/> 靠它还原。</summary>
    public const string AddedNamespaceMarker = "; JASM: added namespace so the in-game menu can toggle this mod";

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
    /// 在 ini 开头补一行 ASCII namespace。3DMigoto 对没有 namespace 的 ini 用路径当命名空间，
    /// 而 Mod 路径通常含中文，菜单里的 <c>$\...\var</c> 会解析失败；补成 ASCII 后就能跨文件访问。
    /// 作者已有 namespace 时什么都不做。写入保持原编码与 BOM。
    /// </summary>
    public static bool TryInsertNamespace(string path, string ns)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Decode(bytes);

        if (text.Contains(AddedNamespaceMarker, StringComparison.Ordinal) || HasNamespaceDirective(text))
            return true;

        var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        var prefix = AddedNamespaceMarker + newLine + "namespace = " + ns + newLine + newLine;
        WriteWithOriginalEncoding(path, prefix + text);
        return true;
    }

    /// <summary>移除 <see cref="TryInsertNamespace"/> 补的行；文件里没有标记时返回 false。</summary>
    public static bool TryRemoveAddedNamespace(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var text = Decode(bytes);
        var newLine = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Split(new[] { newLine }, StringSplitOptions.None).ToList();

        var index = lines.FindIndex(line => line.Contains(AddedNamespaceMarker, StringComparison.Ordinal));
        if (index < 0)
            return false;

        lines.RemoveAt(index);
        if (index < lines.Count && lines[index].TrimStart().StartsWith("namespace", StringComparison.OrdinalIgnoreCase))
            lines.RemoveAt(index);
        if (index < lines.Count && lines[index].Length == 0)
            lines.RemoveAt(index);

        WriteWithOriginalEncoding(path, string.Join(newLine, lines));
        return true;
    }

    private static bool HasNamespaceDirective(string text)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (line.StartsWith('['))
                return false;
            if (line.StartsWith("namespace", StringComparison.OrdinalIgnoreCase)
                && line.Contains('=', StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static void WriteWithOriginalEncoding(string path, string text)
    {
        var bytes = File.ReadAllBytes(path);
        var (encoding, preamble) = DetectEncoding(bytes);
        var payload = preamble.Concat(encoding.GetBytes(text)).ToArray();
        var temp = path + ".jasm.tmp";
        File.WriteAllBytes(temp, payload);
        File.Move(temp, path, overwrite: true);
    }

    private static (Encoding Encoding, byte[] Preamble) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), [0xEF, 0xBB, 0xBF]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode, [0xFF, 0xFE]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode, [0xFE, 0xFF]);

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), []);
        }
        catch (DecoderFallbackException)
        {
        }

        return (TryGetGb18030() ?? Encoding.Latin1, []);
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
