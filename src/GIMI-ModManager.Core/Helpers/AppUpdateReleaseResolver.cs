// 显式 using（不靠 ImplicitUsings）：本文件会被 JASM.AutoUpdater 以「源链接」方式编译进去
// （见 JASM.AutoUpdater.csproj），而那个工程没开 ImplicitUsings。同 Core/Helpers/KeyHelperProtocol.cs。
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GIMI_ModManager.Core.Helpers;

/// <summary>「当前该更新到哪个版本、包在哪」是从哪一侧问到的。</summary>
public enum AppUpdateSource
{
    /// <summary>两侧都没拿到可用记录。</summary>
    None,

    /// <summary>COS 上的 <c>app/update.json</c>（首选通道）。</summary>
    Cos,

    /// <summary>GitHub Releases API（回退通道）。</summary>
    GitHub
}

/// <summary>
/// 决定"更新到哪个版本、包在哪"的**唯一**实现 —— 主程序（<c>UpdateChecker</c> 亮徽标、
/// <c>SingleFileSelfUpdater</c> 下单文件包）与外部更新器（<c>JASM.AutoUpdater</c> 下 folder 包）
/// 共用这一份，靠 <c>JASM.AutoUpdater.csproj</c> 的「源链接」编译进去（同 <c>KeyHelperProtocol</c>）。
///
/// **为什么必须共用**：主程序负责亮"有新版本"的徽标、更新器负责实际下载。两边各自决定"最新版是哪个"
/// 就会出现「徽标说 2.31.0，点下去却去 GitHub 拿了 2.30.0」这种最难查的偏差 —— 它不报错，
/// 只是行为不对。所以策略只有这一份：
///
/// <list type="number">
/// <item>
/// **COS 优先**：<c>cosManifestUrl</c> 非空时先拉清单，能选中一条"非预发布 + 版本号可解析 +
/// 至少有一个下得了的资产"的记录就用它。
/// </item>
/// <item>
/// **GitHub 回退**：清单没配、拉不到、解析不出、或选中不了记录时，退回 GitHub Releases API
/// （语义与迁移前逐字一致：过滤 prerelease，取 tag 版本最大者）。
/// 这也是运营上的回滚开关 —— COS 欠费 / 权限配错 / 被刷流量时，把资产重新挂回 GitHub release
/// 即可让**所有已发布的客户端自动走回退**，不需要发新版。
/// </item>
/// </list>
///
/// 全程不抛异常：任何一种失败都只返回"没找到"，由调用方沿用现有的"本次无更新"路径。
/// 这一点是硬要求 —— <c>UpdateChecker</c> 的后台轮询循环一旦抛异常就永久 break，用户此后再也
/// 收不到更新提示，而且界面上看不出任何异常。
/// </summary>
public static class AppUpdateReleaseResolver
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// 解析结果。<paramref name="Diagnostic"/> 是给日志用的一句说明（不含敏感数据）：
    /// 命中回退通道时会记下"首选通道为什么没用上"，全失败时说明两侧各自的原因。
    /// </summary>
    public sealed record Result(AppUpdateRelease? Release, AppUpdateSource Source, string? Diagnostic)
    {
        public static Result None(string diagnostic) => new(null, AppUpdateSource.None, diagnostic);
    }

    /// <summary>
    /// 按「COS 优先 + GitHub 回退」问出当前的发布记录。返回 <c>Release</c> 为 <c>null</c> 表示
    /// 两侧都没有可用版本，调用方按"本次无更新"处理即可。
    /// <paramref name="httpClient"/> 的生命周期由调用方持有（复用工厂里的具名客户端，别在这里 new）。
    /// </summary>
    public static async Task<Result> ResolveAsync(HttpClient httpClient, string? cosManifestUrl,
        string gitHubReleasesApiUrl, CancellationToken cancellationToken = default)
    {
        string? cosFailure = null;

        if (!string.IsNullOrWhiteSpace(cosManifestUrl))
        {
            var cosRelease = await TryResolveFromManifestAsync(httpClient, cosManifestUrl, cancellationToken)
                .ConfigureAwait(false);

            if (cosRelease is not null)
                return new Result(cosRelease, AppUpdateSource.Cos, null);

            cosFailure = $"COS 清单未给出可用发布记录 ({cosManifestUrl})";
        }

        var gitHubRelease = await TryResolveFromGitHubAsync(httpClient, gitHubReleasesApiUrl, cancellationToken)
            .ConfigureAwait(false);

        if (gitHubRelease is not null)
            return new Result(gitHubRelease, AppUpdateSource.GitHub, cosFailure);

        // 两侧地址都写进诊断：查线上问题时最常问的正是"它到底去问了哪个地址"
        var gitHubFailure = $"未能从 GitHub 取到可用发布记录 ({gitHubReleasesApiUrl})";

        return Result.None(cosFailure is null ? gitHubFailure : $"{cosFailure}；{gitHubFailure}");
    }

    /// <summary>
    /// 首选通道：拉 COS 上的清单并选中最新非预发布版。拉不到 / 解析不出 / 选中不了（含
    /// "选中了但一个资产都没有"）一律返回 <c>null</c>，让调用方走回退。
    /// </summary>
    private static async Task<AppUpdateRelease?> TryResolveFromManifestAsync(HttpClient httpClient,
        string manifestUrl, CancellationToken cancellationToken)
    {
        try
        {
            var json = await httpClient.GetStringAsync(manifestUrl, cancellationToken).ConfigureAwait(false);
            var release = AppUpdateManifestParser.SelectLatest(AppUpdateManifestParser.Parse(json));

            return AppUpdateManifestParser.HasUsableAssets(release) ? release : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // 调用方自己的取消要如实传出去，不能当成"这次没找到"
        }
        catch (Exception)
        {
            // 404 / 403（COS 权限配错）/ 超时 / 返回错误页 XML，都只是"这条通道不可用"
            return null;
        }
    }

    /// <summary>
    /// 回退通道：GitHub Releases API。语义与迁移前逐字一致（过滤 prerelease、取 tag 版本最大者），
    /// 只是把结果整理成清单的模型，好让上层两条通道走同一套下游代码。
    /// </summary>
    private static async Task<AppUpdateRelease?> TryResolveFromGitHubAsync(HttpClient httpClient,
        string releasesApiUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(releasesApiUrl))
            return null;

        try
        {
            var json = await httpClient.GetStringAsync(releasesApiUrl, cancellationToken).ConfigureAwait(false);
            var releases = JsonSerializer.Deserialize<List<GitHubRelease>>(json, JsonOptions);
            if (releases is null)
                return null;

            AppUpdateRelease? best = null;
            Version? bestVersion = null;

            foreach (var release in releases)
            {
                if (release is null || release.Prerelease)
                    continue;

                // GitHub 的 tag 常带 v 前缀，也可能是脏数据；TryParse 不抛，坏 tag 直接跳过。
                if (!AppUpdateManifestParser.TryParseVersion(release.TagName, out var version) || version is null)
                    continue;

                if (bestVersion is not null && version <= bestVersion)
                    continue;

                bestVersion = version;
                best = new AppUpdateRelease
                {
                    // 用规范化后的数字点分，而不是原 tag：清单侧本来就是这个形状，下游就不必再 Trim('v')
                    Version = version.ToString(),
                    Prerelease = false,
                    PublishedAt = release.PublishedAt,
                    NotesUrl = string.IsNullOrWhiteSpace(release.HtmlUrl) ? null : release.HtmlUrl,
                    Assets = (release.Assets ?? new List<GitHubAsset>())
                        .Where(a => a is not null)
                        .Select(a => new AppUpdateAsset
                        {
                            Name = a.Name,
                            Kind = InferKindFromName(a.Name),
                            Url = a.BrowserDownloadUrl,
                            SizeBytes = a.Size
                        })
                        .ToList()
                };
            }

            return AppUpdateManifestParser.HasUsableAssets(best) ? best : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// GitHub 的 API 没有"资产类型"字段，只有文件名 —— 这里按命名约定反推，好在清单与回退两条
    /// 通道上给出同一种 <see cref="AppUpdateAsset.Kind"/>。推不出来的留 <c>null</c>，让上层退回
    /// 名字前缀匹配（<see cref="AppUpdateManifestParser.FindAsset"/> 的第二道匹配）。
    /// </summary>
    private static string? InferKindFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (name.StartsWith("SingleFile_", StringComparison.OrdinalIgnoreCase))
            return AppUpdateAsset.KindSingleFile;

        return name.StartsWith("JASM_", StringComparison.OrdinalIgnoreCase)
            ? AppUpdateAsset.KindFolder
            : null;
    }

    // GitHub Releases API 的响应形状。字段用 JsonPropertyName 显式对齐 snake_case ——
    // PropertyNameCaseInsensitive 只忽略大小写，认不出下划线。
    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("published_at")] public DateTime PublishedAt { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}