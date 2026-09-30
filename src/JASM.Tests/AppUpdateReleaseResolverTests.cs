using System.Net;
using System.Text;
using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="AppUpdateReleaseResolver"/> —— 更新源选择策略：COS 优先、GitHub 回退。
///
/// 这一层是主程序（亮徽标）与更新器（实际下载）共用的唯一决策点，所以每条契约都要钉住：
/// 一旦"徽标说的版本"和"实际下载的版本"来自不同通道，用户看到的就是"点了更新却什么都没发生"，
/// 而且不报错。
/// </summary>
public class AppUpdateReleaseResolverTests
{
    private const string CosUrl = "https://cos.invalid/app/update.json";
    private const string GitHubUrl = "https://api.github.invalid/releases?per_page=2";

    private static readonly string UsableManifestJson = """
    {
      "schemaVersion": 1,
      "releases": [
        {
          "version": "2.31.0",
          "assets": [
            { "name": "SingleFile_JASM_v2.31.0.zip", "kind": "singleFile",
              "url": "https://cos.invalid/app/SingleFile_JASM_v2.31.0.zip", "sizeBytes": 100 }
          ]
        }
      ]
    }
    """;

    private static readonly string GitHubReleasesJson = """
    [
      {
        "tag_name": "v2.31.0",
        "prerelease": false,
        "html_url": "https://github.invalid/releases/tag/v2.31.0",
        "published_at": "2026-09-30T08:00:00Z",
        "assets": [
          { "name": "JASM_v2.31.0.7z", "browser_download_url": "https://github.invalid/JASM_v2.31.0.7z", "size": 200 },
          { "name": "SingleFile_JASM_v2.31.0.zip", "browser_download_url": "https://github.invalid/SingleFile_JASM_v2.31.0.zip", "size": 100 }
        ]
      }
    ]
    """;

    // ---- COS 优先 -----------------------------------------------------------

    [Fact]
    public async Task TheCosManifestWinsAndGitHubIsNeverEvenAsked()
    {
        var handler = new StubHandler(uri => uri.Contains("cos.invalid")
            ? StubHandler.Json(UsableManifestJson)
            : throw new InvalidOperationException($"不该请求 {uri}"));

        var result = await ResolveAsync(handler);

        Assert.Equal(AppUpdateSource.Cos, result.Source);
        Assert.Equal("2.31.0", result.Release?.Version);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public async Task AnEmptyManifestUrlSkipsStraightToGitHub()
    {
        var handler = new StubHandler(uri => StubHandler.Json(GitHubReleasesJson));

        var result = await AppUpdateReleaseResolver.ResolveAsync(NewClient(handler), "   ", GitHubUrl);

        Assert.Equal(AppUpdateSource.GitHub, result.Source);
        Assert.Equal("2.31.0", result.Release?.Version);
    }

    // ---- 回退触发条件 -------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)] // COS 权限配错时就是这个，别让它变成"更新功能消失"
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AFailingCosManifestFallsBackToGitHub(HttpStatusCode status)
    {
        var handler = new StubHandler(uri => uri.Contains("cos.invalid")
            ? new HttpResponseMessage(status)
            : StubHandler.Json(GitHubReleasesJson));

        var result = await ResolveAsync(handler);

        Assert.Equal(AppUpdateSource.GitHub, result.Source);
        Assert.NotNull(result.Release);
        // 回退时要把"首选通道为什么没用上"带到日志里，否则线上只能看到"走了 GitHub"这一个事实
        Assert.Contains(CosUrl, result.Diagnostic);
    }

    [Theory]
    [InlineData("<html>404 Not Found</html>")]
    [InlineData("{ \"releases\": [ { \"version\": ")]
    [InlineData("{ \"schemaVersion\": 1, \"releases\": [] }")]
    public async Task AnUnusableCosBodyFallsBackToGitHub(string cosBody)
    {
        var handler = new StubHandler(uri => uri.Contains("cos.invalid")
            ? StubHandler.Json(cosBody)
            : StubHandler.Json(GitHubReleasesJson));

        var result = await ResolveAsync(handler);

        Assert.Equal(AppUpdateSource.GitHub, result.Source);
    }

    // 清单列了版本却没传包（维护者漏传是常事）：当成"这份清单不算数"退回 GitHub，
    // 比让用户点更新后才看到"找不到包"要好。
    [Fact]
    public async Task ACosReleaseWithNoUsableAssetFallsBackToGitHub()
    {
        const string manifestWithoutAssets = """
        { "releases": [ { "version": "2.31.0", "assets": [ { "name": "x.zip" } ] } ] }
        """;
        var handler = new StubHandler(uri => uri.Contains("cos.invalid")
            ? StubHandler.Json(manifestWithoutAssets)
            : StubHandler.Json(GitHubReleasesJson));

        var result = await ResolveAsync(handler);

        Assert.Equal(AppUpdateSource.GitHub, result.Source);
    }

    // ---- GitHub 回退通道的形状 ----------------------------------------------

    [Fact]
    public async Task TheGitHubFallbackNormalisesVersionsAndInfersAssetKinds()
    {
        var handler = new StubHandler(uri => StubHandler.Json(GitHubReleasesJson));

        var result = await AppUpdateReleaseResolver.ResolveAsync(NewClient(handler), null, GitHubUrl);
        var release = result.Release!;

        // tag 的 v 前缀要在这一层剥掉：下游（比对当前版本、展示）都按数字点分来
        Assert.Equal("2.31.0", release.Version);
        Assert.False(release.Prerelease);
        Assert.Equal("https://github.invalid/releases/tag/v2.31.0", release.NotesUrl);

        // GitHub 没有 kind 字段，按命名约定反推 —— 两条通道给出同一种 Kind，下游才不分叉。
        // 两个包名都以 JASM_ 结尾那一段重叠，所以这里真正验的是 Kind 推对了、没被前缀匹配带偏。
        var folderAsset = AppUpdateManifestParser.FindAsset(release, AppUpdateAsset.KindFolder, "JASM_");
        var singleFileAsset = AppUpdateManifestParser.FindAsset(release, AppUpdateAsset.KindSingleFile,
            "SingleFile_JASM_");

        Assert.Equal("JASM_v2.31.0.7z", folderAsset?.Name);
        Assert.Equal(AppUpdateAsset.KindFolder, folderAsset?.Kind);
        Assert.Equal("SingleFile_JASM_v2.31.0.zip", singleFileAsset?.Name);
        Assert.Equal(AppUpdateAsset.KindSingleFile, singleFileAsset?.Kind);
    }

    [Fact]
    public async Task TheGitHubFallbackPicksTheHighestNonPrereleaseTag()
    {
        const string json = """
        [
          { "tag_name": "v2.32.0", "prerelease": true,
            "assets": [ { "name": "JASM_v2.32.0.7z", "browser_download_url": "https://github.invalid/p.7z" } ] },
          { "tag_name": "v2.31.0", "prerelease": false,
            "assets": [ { "name": "JASM_v2.31.0.7z", "browser_download_url": "https://github.invalid/a.7z" } ] },
          { "tag_name": "v2.30.0", "prerelease": false,
            "assets": [ { "name": "JASM_v2.30.0.7z", "browser_download_url": "https://github.invalid/b.7z" } ] }
        ]
        """;
        var handler = new StubHandler(uri => StubHandler.Json(json));

        var result = await AppUpdateReleaseResolver.ResolveAsync(NewClient(handler), null, GitHubUrl);

        Assert.Equal("2.31.0", result.Release?.Version);
    }

    // 迁移前的代码在这里是 new Version(tag)，脏 tag 会抛 → 冒泡到轮询循环 → 更新检查永久停摆。
    [Fact]
    public async Task TheGitHubFallbackSkipsDirtyTagsInsteadOfThrowing()
    {
        const string json = """
        [
          { "tag_name": "nightly-build", "prerelease": false,
            "assets": [ { "name": "JASM_x.7z", "browser_download_url": "https://github.invalid/n.7z" } ] },
          { "tag_name": "", "prerelease": false,
            "assets": [ { "name": "JASM_y.7z", "browser_download_url": "https://github.invalid/e.7z" } ] },
          { "tag_name": "v2.31.0", "prerelease": false,
            "assets": [ { "name": "JASM_v2.31.0.7z", "browser_download_url": "https://github.invalid/a.7z" } ] }
        ]
        """;
        var handler = new StubHandler(uri => StubHandler.Json(json));

        var result = await AppUpdateReleaseResolver.ResolveAsync(NewClient(handler), null, GitHubUrl);

        Assert.Equal("2.31.0", result.Release?.Version);
    }

    // ---- 两侧都失败 ---------------------------------------------------------

    [Fact]
    public async Task BothChannelsFailingYieldsNoneWithADiagnostic()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await ResolveAsync(handler);

        Assert.Null(result.Release);
        Assert.Equal(AppUpdateSource.None, result.Source);
        Assert.Contains(CosUrl, result.Diagnostic);
        Assert.Contains(GitHubUrl, result.Diagnostic);
    }

    // 调用方自己的取消必须如实传出，不能被 catch-all 吞成"这次没找到" ——
    // 吞掉的话应用退出时的取消会变成一次静默的空结果。
    [Fact]
    public async Task ACancelledCallerTokenPropagates()
    {
        var handler = new StubHandler(uri => StubHandler.Json(UsableManifestJson));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppUpdateReleaseResolver.ResolveAsync(NewClient(handler), CosUrl, GitHubUrl, cts.Token));
    }

    // ---- 测试脚手架 ---------------------------------------------------------

    private static Task<AppUpdateReleaseResolver.Result> ResolveAsync(StubHandler handler) =>
        AppUpdateReleaseResolver.ResolveAsync(NewClient(handler), CosUrl, GitHubUrl);

    private static HttpClient NewClient(StubHandler handler) => new(handler) { BaseAddress = null };

    /// <summary>
    /// 极简 HttpMessageHandler 桩：按请求 URI 现造响应。<paramref name="responder"/> 抛异常即断言
    /// "这个地址不该被请求"（用来证明 COS 命中时不会再去碰 GitHub）。
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _responder;

        public StubHandler(Func<string, HttpResponseMessage> responder) => _responder = responder;

        public static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_responder(request.RequestUri?.ToString() ?? string.Empty));
        }
    }
}