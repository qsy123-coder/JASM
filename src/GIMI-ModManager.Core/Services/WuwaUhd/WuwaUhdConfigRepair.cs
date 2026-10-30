using System.Text;

namespace GIMI_ModManager.Core.Services.WuwaUhd;

/// <summary>一条 INI 设置：目标段、键、值。写入时按键覆盖，不碰同段其它行。</summary>
public sealed record WuwaIniSetting(string Section, string Key, string Value);

/// <summary>
/// 鸣潮「UHD 贴图修复 / 画质配置」的写回逻辑（对应 Rocket 的 XXMI 配置方式）。
///
/// Rocket 在 <c>FUN_1402ce6a0</c> 里确认会写 <c>Engine.ini</c> 的：
/// <code>
/// [/Script/Engine.RendererSettings]
/// r.RayTracing.LoadConfig=0
/// r.MaxAnisotropy=1
/// r.Streaming.HiddenPrimitiveScale=20
/// </code>
/// 以及 <c>GameUserSettings.ini</c> 的 <c>ImageDetail=3</c> / <c>ImageQuality=3</c>（0x1402CE82D 一带）。
/// 这些项用于避免光追/各向异性/流送把 mod 贴图降级或截断，是“UHD 下贴图错误”的配置侧修复；
/// 哈希侧的修复仍由 Wuwa_Mod_Fixer 的 <c>--derived-hashes</c> / <c>--stable-texture</c> 完成。
///
/// 这里只做**纯文本变换**：保留注释、空行、其它段与键；同一段内按 key 覆盖；缺失段/键则补。
/// 备份与落盘由调用方负责。
/// </summary>
public static class WuwaUhdConfigRepair
{
    public const string RendererSection = "[/Script/Engine.RendererSettings]";
    public const string GameUserSettingsSection = "[/Script/Engine.GameUserSettings]";

    /// <summary>Engine.ini 的修复项。</summary>
    public static IReadOnlyList<WuwaIniSetting> EngineSettings { get; } =
    [
        new(RendererSection, "r.RayTracing.LoadConfig", "0"),
        new(RendererSection, "r.MaxAnisotropy", "1"),
        new(RendererSection, "r.Streaming.HiddenPrimitiveScale", "20")
    ];

    /// <summary>GameUserSettings.ini 的修复项。</summary>
    public static IReadOnlyList<WuwaIniSetting> GameUserSettingsSettings { get; } =
    [
        new(GameUserSettingsSection, "ImageDetail", "3"),
        new(GameUserSettingsSection, "ImageQuality", "3"),
        new(GameUserSettingsSection, "RayTracing", "0"),
        new(GameUserSettingsSection, "RayTracedReflection", "0"),
        new(GameUserSettingsSection, "RayTracedGI", "0")
    ];

    /// <summary>
    /// 把设置写进 INI 文本。返回新文本；若所有项已是目标值，返回内容与输入等价（仍可能规范化换行）。
    /// </summary>
    public static string Apply(string? originalText, IEnumerable<WuwaIniSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var newline = (originalText?.Contains("\r\n", StringComparison.Ordinal) ?? false) ? "\r\n" : "\n";
        var lines = (originalText ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();

        // 末尾换行会产生一个空串元素；先去掉，统一在最后补
        if (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        foreach (var group in settings.GroupBy(s => s.Section, StringComparer.OrdinalIgnoreCase))
            ApplySection(lines, group.Key, group.ToList());

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
            lines.RemoveAt(lines.Count - 1);

        var builder = new StringBuilder();
        foreach (var line in lines)
            builder.Append(line).Append(newline);

        return builder.ToString();
    }

    /// <summary>所有设置是否都已是目标值（用于判断是否需要写盘）。</summary>
    public static bool IsApplied(string? text, IEnumerable<WuwaIniSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var lines = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (var setting in settings)
        {
            if (!TryFindSettingLine(lines, setting.Section, setting.Key, out var index))
                return false;

            if (!string.Equals(ExtractValue(lines[index]), setting.Value, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static void ApplySection(List<string> lines, string section, List<WuwaIniSetting> settings)
    {
        var headerIndex = FindSectionHeader(lines, section);

        if (headerIndex < 0)
        {
            // 新段：与上文之间留一个空行，避免粘在上一行后面
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add(string.Empty);

            lines.Add(section);
            foreach (var setting in settings)
                lines.Add($"{setting.Key}={setting.Value}");

            return;
        }

        foreach (var setting in settings)
        {
            var header = FindSectionHeader(lines, section);
            var end = FindSectionEnd(lines, header);

            var keyIndex = -1;
            for (var i = header + 1; i < end; i++)
            {
                if (TryParseKey(lines[i], out var key) &&
                    string.Equals(key, setting.Key, StringComparison.OrdinalIgnoreCase))
                {
                    keyIndex = i;
                    break;
                }
            }

            if (keyIndex >= 0)
            {
                var indent = lines[keyIndex][..(lines[keyIndex].Length - lines[keyIndex].TrimStart().Length)];
                lines[keyIndex] = $"{indent}{setting.Key}={setting.Value}";
                continue;
            }

            // 没有这个键：插到段内最后一个非空行之后（保留段尾空行）
            var insertAt = header + 1;
            for (var i = end - 1; i > header; i--)
            {
                if (!string.IsNullOrWhiteSpace(lines[i]))
                {
                    insertAt = i + 1;
                    break;
                }
            }

            lines.Insert(insertAt, $"{setting.Key}={setting.Value}");
        }
    }

    private static bool TryFindSettingLine(string[] lines, string section, string key, out int index)
    {
        index = -1;
        var header = FindSectionHeader(lines, section);
        if (header < 0)
            return false;

        var end = FindSectionEnd(lines, header);
        for (var i = header + 1; i < end; i++)
        {
            if (TryParseKey(lines[i], out var candidate) &&
                string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                return true;
            }
        }

        return false;
    }

    private static int FindSectionHeader(IReadOnlyList<string> lines, string section)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (string.Equals(lines[i].Trim(), section, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static int FindSectionEnd(IReadOnlyList<string> lines, int headerIndex)
    {
        for (var i = headerIndex + 1; i < lines.Count; i++)
        {
            if (IsSectionHeader(lines[i]))
                return i;
        }

        return lines.Count;
    }

    private static bool IsSectionHeader(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']';
    }

    /// <summary>解析 <c>key=value</c> 行；注释、段头、空行都不算。</summary>
    private static bool TryParseKey(string line, out string key)
    {
        key = string.Empty;
        var trimmed = line.TrimStart();

        if (trimmed.Length == 0 || trimmed[0] is ';' or '#' || trimmed[0] == '[')
            return false;

        var separator = trimmed.IndexOf('=');
        if (separator <= 0)
            return false;

        key = trimmed[..separator].Trim();
        return key.Length > 0;
    }

    private static string ExtractValue(string line)
    {
        var separator = line.IndexOf('=');
        return separator < 0 ? string.Empty : line[(separator + 1)..].Trim();
    }
}
