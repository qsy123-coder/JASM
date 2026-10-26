namespace GIMI_ModManager.Core.Services.WuwaModFixer;

/// <summary>
/// 配置更新源的顺序与主机白名单。
///
/// Gitee 排第一是因为国内直连更稳；GitHub raw 是回退。允许的主机写死在这里，
/// 是为了防止上游/中间人把重定向指到一个陌生地址 —— 我们只下载 JSON，不执行它，
/// 但「从哪里取」仍然应该由我们说了算。
/// </summary>
public static class WuwaConfigUpdateSources
{
    /// <summary>按顺序尝试的源。</summary>
    public static IReadOnlyList<Uri> Sources { get; } =
    [
        WuwaModFixerProtocol.GiteeConfigUrl,
        WuwaModFixerProtocol.GitHubConfigUrl
    ];

    /// <summary>允许的主机（大小写不敏感）。</summary>
    private static readonly string[] AllowedHosts =
    [
        "gitee.com",
        "github.com",
        "raw.githubusercontent.com",
        "githubusercontent.com"
    ];

    /// <summary>只接受 HTTPS + 白名单主机。</summary>
    public static bool IsAllowed(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            return false;

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        return AllowedHosts.Any(host =>
            string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 解析重定向：相对地址按当前地址补全，结果必须仍满足 <see cref="IsAllowed"/>。
    /// 返回 false 表示这一跳不可跟随，调用方应中止，而不是「先跟过去再说」。
    /// </summary>
    public static bool TryResolveRedirect(Uri current, string? location, out Uri next)
    {
        next = current;

        if (string.IsNullOrWhiteSpace(location)
            || !Uri.TryCreate(current, location, out var resolved))
            return false;

        if (!IsAllowed(resolved))
            return false;

        next = resolved;
        return true;
    }
}