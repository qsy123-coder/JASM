// 显式 using（不靠 ImplicitUsings）：本文件会被 JASM.AutoUpdater 以「源链接」方式编译进去
// （见 JASM.AutoUpdater.csproj），而那个工程没开 ImplicitUsings。同 Core/Helpers/KeyHelperProtocol.cs。
using System;
using System.Linq;
using System.Text.Json;

namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 解析 COS 上的更新清单，并挑出「最新非预发布版 + 指定类型的产物」。
///
/// 全是纯函数，且**绝不抛异常**：清单是远端数据，拿到的可能是 CDN 的 404 页面、被截断的半截
/// JSON、或者维护者手滑写错的字段。任何一种都只该让这一次「没找到更新」，而不是把调用方的
/// 更新检查循环整个打挂（<c>UpdateChecker</c> 的后台循环一旦抛异常就 break，用户此后永远
/// 收不到更新提示，且界面上看不出任何异常）。
/// </summary>
public static class AppUpdateManifestParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>解析清单 JSON。空串 / 畸形 JSON / 结构不符一律返回 <c>null</c>。</summary>
    public static AppUpdateManifest? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<AppUpdateManifest>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 取清单里版本号最高的那条记录，默认跳过预发布版。
    ///
    /// 版本号解析不出来的条目直接忽略 —— 一条坏数据不该让整份清单作废，也不该被当成
    /// "版本 0.0.0" 而永远排在最后（那反而会掩盖它其实是坏数据这一点）。
    /// </summary>
    public static AppUpdateRelease? SelectLatest(AppUpdateManifest? manifest, bool includePrerelease = false)
    {
        if (manifest?.Releases is null)
            return null;

        AppUpdateRelease? best = null;
        var bestVersion = new Version(0, 0, 0);

        foreach (var release in manifest.Releases)
        {
            if (release is null)
                continue;

            if (release.Prerelease && !includePrerelease)
                continue;

            if (!TryParseVersion(release.Version, out var version) || version is null)
                continue;

            if (best is null || version > bestVersion)
            {
                best = release;
                bestVersion = version;
            }
        }

        return best;
    }

    /// <summary>
    /// 在一条发布记录里找指定类型的产物：先按 <see cref="AppUpdateAsset.Kind"/> 精确匹配
    /// （忽略大小写），匹配不到再退回名字前缀 <paramref name="namePrefix"/>。
    ///
    /// 两道匹配都要 URL 可用：清单里写了条目但没有可用地址，等于没写 —— 挑中它只会在下载那步
    /// 才炸，错误信息还离根因很远。
    /// </summary>
    public static AppUpdateAsset? FindAsset(AppUpdateRelease? release, string kind, string namePrefix)
    {
        if (release?.Assets is null)
            return null;

        var usable = release.Assets.Where(HasUsableUrl).ToList();

        var byKind = usable.FirstOrDefault(a =>
            string.Equals(a.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (byKind is not null)
            return byKind;

        return usable.FirstOrDefault(a =>
            a.Name?.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// 这条记录里至少有一个下得了的资产。<see cref="AppUpdateReleaseResolver"/> 用它判定
    /// "这份清单算不算数"：清单里列了版本却没传包（维护者漏传是常事）时，宁可当成没有、退回
    /// GitHub，也不要让用户点了更新才发现"找不到包"。
    /// </summary>
    public static bool HasUsableAssets(AppUpdateRelease? release) =>
        release?.Assets is not null && release.Assets.Any(HasUsableUrl);

    /// <summary>URL 是绝对的 HTTP(S) 地址才算可用 —— 空值、相对路径都下不了。</summary>
    public static bool HasUsableUrl(AppUpdateAsset? asset) =>
        asset is not null &&
        Uri.TryCreate(asset.Url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// 宽松解析版本号：容忍一个前导 <c>v</c>（维护者顺手从 GitHub tag 抄过来时很常见），
    /// 其余情况按 <see cref="Version.TryParse(string?, out Version?)"/> 的规则来，解析不出就返回 false。
    ///
    /// 这就是它与旧 GitHub 分支的关键区别：那边是 <c>new Version(tag)</c>，遇到脏 tag 会直接抛。
    /// </summary>
    public static bool TryParseVersion(string? value, out Version? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return Version.TryParse(value.Trim().TrimStart('v', 'V'), out version);
    }
}