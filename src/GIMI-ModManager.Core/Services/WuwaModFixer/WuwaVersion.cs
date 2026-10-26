using System.Globalization;

namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>
/// 上游版本号的解析与比较。
///
/// 上游把版本写成 <c>3.7</c> / <c>3.7.1</c> / <c>v3.7.1.0</c> 多种形态，且可能带
/// <c>-beta</c> 之类的后缀；直接交给 <see cref="Version.TryParse"/> 会漏掉一部分。
/// 这里只取每段的前导数字、缺省段补 0，够用来做「配置要求的最低版本 vs 内置 CLI」这一种比较。
/// </summary>
public static class WuwaVersion
{
    /// <summary>
    /// 解析成 4 段版本。至少要有两段（避免把 "3" 这种随便的数字当成版本），
    /// 段内允许尾随任意非数字后缀（<c>3.7.1-beta</c> 取 3.7.1）。
    /// </summary>
    public static bool TryParse(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var span = text.Trim();
        if (span.StartsWith('v') || span.StartsWith('V'))
            span = span[1..];

        var parts = span.Split('.');
        if (parts.Length < 2)
            return false;

        var numbers = new int[4];
        for (var i = 0; i < parts.Length && i < numbers.Length; i++)
        {
            if (!TryParseLeadingNumber(parts[i], out numbers[i]))
                return false;
        }

        version = new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        return true;
    }

    /// <summary>
    /// 比较两个版本，返回负数/0/正数。任一侧解析失败按 0.0.0.0 处理 ——
    /// 调用方需要的是「能不能用」而不是抛异常，坏版本号应当退化成最旧，让校验把它拦下来。
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var leftVersion = TryParse(left, out var parsedLeft) ? parsedLeft : new Version(0, 0, 0, 0);
        var rightVersion = TryParse(right, out var parsedRight) ? parsedRight : new Version(0, 0, 0, 0);
        return leftVersion.CompareTo(rightVersion);
    }

    /// <summary>取每段开头连续的数字；<c>7-beta</c> → 7，<c>beta</c> → 失败。</summary>
    private static bool TryParseLeadingNumber(string segment, out int value)
    {
        value = 0;

        var length = 0;
        while (length < segment.Length && char.IsAsciiDigit(segment[length]))
            length++;

        return length > 0
               && int.TryParse(segment.AsSpan(0, length), NumberStyles.None, CultureInfo.InvariantCulture,
                   out value);
    }
}