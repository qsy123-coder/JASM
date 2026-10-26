namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>
/// 释放决策（纯逻辑，不做 IO）。
///
/// CLI 与配置的「该不该重写」分开判断：
/// - CLI 看「版本 + SHA256」：固定版本也可能被重新打包，只比版本号不够；
/// - 配置看「版本 + 本地是否仍然合法」：用户手动点过「更新配置」后，程序升级**不能**把它降回内置版本。
/// </summary>
public static class WuwaModFixerProvisioning
{
    /// <summary>
    /// 是否需要把内嵌 CLI 释放到运行目录。
    /// 内嵌资源不存在时返回 false：开发机构建可能没跑取资源脚本，此时保留已有副本，交给上层报「引擎不可用」。
    /// </summary>
    public static bool ShouldProvision(
        bool embeddedExists,
        bool extractedExists,
        string? marker,
        string expectedVersion,
        string expectedSha256,
        string? actualSha256)
    {
        if (!embeddedExists)
            return false;

        if (!extractedExists)
            return true;

        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!WuwaModFixerProtocol.TryParseMarker(marker, out var markerVersion, out var markerSha))
            return true;

        return !string.Equals(markerVersion, expectedVersion, StringComparison.Ordinal)
               || !string.Equals(markerSha, expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 是否用内嵌配置替换本地配置。
    /// 本地非法（用户手改坏/写到一半断电）时替换；本地版本不低于内嵌时保留本地。
    /// </summary>
    public static bool ShouldReplaceConfig(
        string? localCurrentVersion,
        string embeddedCurrentVersion,
        bool localValid)
    {
        if (!localValid)
            return true;

        if (!WuwaVersion.TryParse(localCurrentVersion, out _))
            return true;

        return WuwaVersion.Compare(embeddedCurrentVersion, localCurrentVersion) > 0;
    }
}