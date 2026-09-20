namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 从 3Dmigoto / XXMI 的 <c>d3dx.ini</c> 里取出被注入的游戏进程名。
///
/// 这是 JASM 里唯一「零配置」的游戏身份来源：<c>d3dx.ini</c> 与 <c>Mods</c> 同级，
/// 位于用户已经配好的 <c>ModManagerOptions.GimiRootFolderPath</c> 正下方，
/// 其 <c>[Loader]</c> 段的 <c>target =</c> 就是游戏 exe。
/// 对比之下 <c>GameInfo</c> 只记注入器（<c>3DMigoto Loader.exe</c>），而「游戏启动命令」实际是 XXMI Launcher。
///
/// 返回值**不含 <c>.exe</c> 后缀** —— <c>Process.GetProcessesByName</c> 要的就是这种形式，
/// 带上后缀会一个进程都找不到。
/// </summary>
public static class D3dxIniTargetParser
{
    private const string TargetKey = "target";
    private const string LoaderSectionName = "Loader";
    private const string ExeSuffix = ".exe";

    /// <summary>
    /// 解析游戏进程名（不含 <c>.exe</c>）。取不到时返回 <c>null</c>。
    /// 优先取 <c>[Loader]</c> 段里的 <c>target</c>；整份文件都没有该段时，退化为第一个带 <c>.exe</c> 的 <c>target</c>。
    /// </summary>
    public static string? ParseTargetProcessName(string? iniContent)
    {
        if (string.IsNullOrWhiteSpace(iniContent))
            return null;

        string? loaderHit = null; // [Loader] 段里的 target（权威来源）
        string? anyHit = null;    // 文件里任何位置的 target（兜底）
        var inLoaderSection = false;

        foreach (var rawLine in iniContent.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            // 整行注释（3Dmigoto 用 ; ，也有作者用 #）
            if (line.StartsWith(';') || line.StartsWith('#')) continue;

            if (IniConfigHelpers.IsSection(line))
            {
                inLoaderSection = IniConfigHelpers.IsSection(line, LoaderSectionName);
                continue;
            }

            var processName = ToProcessName(GetValue(line, TargetKey));
            if (processName is null) continue;

            anyHit ??= processName;
            if (inLoaderSection) loaderHit ??= processName;
        }

        return loaderHit ?? anyHit;
    }

    /// <summary>取 <c>key = value</c> 的值部分；key 不匹配（或该行没有 "="）时返回 <c>null</c>。</summary>
    private static string? GetValue(string line, string key)
    {
        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0) return null;

        // 不用 IniConfigHelpers.IsIniKey：它是 StartsWith，会把 "targets =" 也算命中
        var actualKey = line[..separatorIndex].Trim();
        if (!actualKey.Equals(key, StringComparison.OrdinalIgnoreCase)) return null;

        return line[(separatorIndex + 1)..].Trim();
    }

    /// <summary>把 <c>target =</c> 的值规整成进程名：去注释 / 去引号 / 去目录 / 去 <c>.exe</c>。</summary>
    private static string? ToProcessName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        // 行尾注释：进程名里不可能出现 ; 或 #
        var commentIndex = value.IndexOfAny([';', '#']);
        if (commentIndex >= 0) value = value[..commentIndex];

        value = value.Trim().Trim('"', '\'').Trim();
        if (value.Length == 0) return null;

        // 有的 d3dx.ini 会写成完整路径
        var fileName = Path.GetFileName(value);
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        // 没有 .exe 后缀的值不可信（可能命中了别的同名配置），宁可当作没找到
        if (!fileName.EndsWith(ExeSuffix, StringComparison.OrdinalIgnoreCase)) return null;

        var processName = fileName[..^ExeSuffix.Length].Trim();
        return processName.Length == 0 ? null : processName;
    }
}