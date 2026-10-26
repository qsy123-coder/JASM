namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>
/// 与上游 Wuwa_Mod_Fixer 的固定契约：CLI 版本、内嵌资源名、释放文件名、标记格式。
///
/// 这些常量同时被三处消费：主工程 csproj 的 <c>LogicalName</c>、运行时释放器、发布脚本。
/// 任何一处改名都要三处一起改 —— 资源名对不上时 <c>GetManifestResourceStream</c> 只会返回 null，
/// 表现成「修复引擎不可用」，不会在编译期报错。
/// </summary>
public static class WuwaModFixerProtocol
{
    /// <summary>内置的 CLI 版本。与上游 release asset 的版本号一致，不带 v 前缀。</summary>
    public const string CliVersion = "3.7.0";

    /// <summary>
    /// 内置 CLI 的 SHA256（小写十六进制）。固定版本的意义就在于这个哈希：
    /// 上游同名资产重新上传、或本地文件被杀软改写，都必须被发现。
    /// </summary>
    public const string CliSha256 = "737c71507a8bc43a0794cace0aba07bccee6967de95cfb29291c9700cc85a370";

    /// <summary>CLI 可执行文件名（释放到运行目录后使用）。</summary>
    public const string CliFileName = "wuwa-mod-cli.exe";

    /// <summary>维修配置文件名（通过 <c>--config</c> 显式传给 CLI）。</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>CLI 释放标记文件名，内容形如 <c>3.7.0|&lt;sha256&gt;</c>。</summary>
    public const string CliMarkerFileName = "wuwa-mod-cli.version";

    /// <summary>配置版本标记文件名，仅用于诊断与展示。</summary>
    public const string ConfigMarkerFileName = "config.version";

    /// <summary>CLI 的内嵌资源逻辑名，必须与 csproj 的 <c>LogicalName</c> 完全一致。</summary>
    public const string CliEmbeddedResourceName = "JASM.WuwaModFixer.wuwa-mod-cli.exe";

    /// <summary>内置配置的内嵌资源逻辑名。</summary>
    public const string ConfigEmbeddedResourceName = "JASM.WuwaModFixer.config.json";

    /// <summary>上游配置的下载源（Gitee 优先，GitHub 回退）。</summary>
    public static readonly Uri GiteeConfigUrl =
        new("https://gitee.com/moonholder/Wuwa_Mod_Fixer/raw/main/config.json");

    /// <summary>上游配置的 GitHub 回退源。</summary>
    public static readonly Uri GitHubConfigUrl =
        new("https://raw.githubusercontent.com/Moonholder/Wuwa_Mod_Fixer/main/config.json");

    /// <summary>把版本与哈希拼成标记文本。</summary>
    public static string BuildMarker(string version, string sha256) => $"{version}|{sha256}";

    /// <summary>
    /// 解析标记文本。格式不对一律返回 false，由调用方当作「没有标记」处理 ——
    /// 标记坏了最多多释放一次文件，不能因此让浮窗打不开。
    /// </summary>
    public static bool TryParseMarker(string? marker, out string version, out string sha256)
    {
        version = string.Empty;
        sha256 = string.Empty;

        if (string.IsNullOrWhiteSpace(marker))
            return false;

        var parts = marker.Trim().Split('|');
        if (parts.Length != 2)
            return false;

        version = parts[0].Trim();
        sha256 = parts[1].Trim().ToLowerInvariant();

        return version.Length > 0 && sha256.Length == 64;
    }
}