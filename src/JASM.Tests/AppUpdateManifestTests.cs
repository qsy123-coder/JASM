using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="AppUpdateManifestParser"/> 与 <see cref="AppUpdateDownloadProgress"/>。
///
/// 这一组测试盯的是**远端数据不可信**这件事：清单是维护者手工传到 COS 上的 JSON，客户端拿到
/// 的可能是 CDN 的 404 页面、被截断的半截内容、或者字段写错的一版。任何一种都只能让"这次没找到
/// 更新"，不能让 <c>UpdateChecker</c> 的后台循环抛异常 —— 那个循环一抛就 break，用户此后永远
/// 收不到更新提示，而且界面上完全看不出异常。
/// </summary>
public class AppUpdateManifestTests
{
    private const string ValidJson = """
    {
      "schemaVersion": 1,
      "releases": [
        {
          "version": "2.31.0",
          "prerelease": false,
          "publishedAt": "2026-09-30T08:00:00Z",
          "notesUrl": "https://example.invalid/tag/v2.31.0",
          "assets": [
            { "name": "JASM_v2.31.0.7z", "kind": "folder",
              "url": "https://example.invalid/app/JASM_v2.31.0.7z", "sizeBytes": 148726913, "sha256": "aa" },
            { "name": "SingleFile_JASM_v2.31.0.zip", "kind": "singleFile",
              "url": "https://example.invalid/app/SingleFile_JASM_v2.31.0.zip", "sizeBytes": 96384012, "sha256": "bb" }
          ]
        },
        {
          "version": "2.30.0",
          "prerelease": false,
          "assets": [
            { "name": "SingleFile_JASM_v2.30.0.zip", "kind": "singleFile",
              "url": "https://example.invalid/app/SingleFile_JASM_v2.30.0.zip" }
          ]
        }
      ]
    }
    """;

    // ---- Parse --------------------------------------------------------------

    [Fact]
    public void AValidManifestIsParsedWithItsReleasesAndAssets()
    {
        var manifest = AppUpdateManifestParser.Parse(ValidJson);

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.SchemaVersion);
        Assert.Equal(2, manifest.Releases.Count);
        Assert.Equal("2.31.0", manifest.Releases[0].Version);
        Assert.Equal(2, manifest.Releases[0].Assets.Count);
        Assert.Equal(148726913, manifest.Releases[0].Assets[0].SizeBytes);
        Assert.Equal("https://example.invalid/tag/v2.31.0", manifest.Releases[0].NotesUrl);
    }

    // 空响应不是一个错误路径，而是常态：CDN 上还没传清单、或者命中缓存返回了空体。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyBodyYieldsNullRatherThanThrowing(string? json)
    {
        Assert.Null(AppUpdateManifestParser.Parse(json));
    }

    // 半截 JSON（下载被截断）与 CDN 的错误页：都必须安静地变成 null。
    [Theory]
    [InlineData("{ \"releases\": [ { \"version\": \"2.31.0\" ")]
    [InlineData("<html><body>404 Not Found</body></html>")]
    [InlineData("[]")]
    [InlineData("42")]
    public void AMalformedOrUnexpectedlyShapedBodyYieldsNull(string json)
    {
        Assert.Null(AppUpdateManifestParser.Parse(json));
    }

    // 少字段不该让整份清单失效：缺的字段按默认值走，能用的那几条照常能用。
    [Fact]
    public void AManifestWithMissingFieldsStillParses()
    {
        var manifest = AppUpdateManifestParser.Parse("""{ "releases": [ { "version": "2.31.0" } ] }""");

        Assert.NotNull(manifest);
        var release = Assert.Single(manifest!.Releases);
        Assert.False(release.Prerelease);
        Assert.Empty(release.Assets);
        Assert.Null(release.NotesUrl);
    }

    // 字段名大小写不该成为陷阱：手写清单时写成 Version / Releases 很常见。
    [Fact]
    public void PropertyNamesAreMatchedCaseInsensitively()
    {
        var manifest = AppUpdateManifestParser.Parse("""{ "Releases": [ { "Version": "2.31.0" } ] }""");

        Assert.NotNull(manifest);
        Assert.Equal("2.31.0", Assert.Single(manifest!.Releases).Version);
    }

    // ---- SelectLatest -------------------------------------------------------

    [Fact]
    public void SelectLatestPicksTheHighestVersionNotMerelyTheFirstEntry()
    {
        // 顺序反了也要挑对：清单靠人维护，"降序排列"是约定而不是保证。
        var manifest = AppUpdateManifestParser.Parse("""
        { "releases": [ { "version": "2.30.0" }, { "version": "2.31.0" }, { "version": "2.9.0" } ] }
        """);

        Assert.Equal("2.31.0", AppUpdateManifestParser.SelectLatest(manifest)?.Version);
    }

    [Fact]
    public void SelectLatestSkipsPrereleasesByDefault()
    {
        var manifest = AppUpdateManifestParser.Parse("""
        { "releases": [ { "version": "2.32.0", "prerelease": true }, { "version": "2.31.0" } ] }
        """);

        Assert.Equal("2.31.0", AppUpdateManifestParser.SelectLatest(manifest)?.Version);
    }

    [Fact]
    public void SelectLatestCanIncludePrereleasesWhenAsked()
    {
        var manifest = AppUpdateManifestParser.Parse("""
        { "releases": [ { "version": "2.32.0", "prerelease": true }, { "version": "2.31.0" } ] }
        """);

        Assert.Equal("2.32.0",
            AppUpdateManifestParser.SelectLatest(manifest, includePrerelease: true)?.Version);
    }

    // 关键回归点：旧代码这里是 new Version(tag)，脏版本号会直接抛。
    [Theory]
    [InlineData("not-a-version")]
    [InlineData("2.31.0-beta.1")]
    [InlineData("")]
    [InlineData(null)]
    public void SelectLatestIgnoresEntriesWhoseVersionCannotBeParsed(string? garbage)
    {
        var manifest = new AppUpdateManifest
        {
            Releases = new List<AppUpdateRelease>
            {
                new() { Version = garbage },
                new() { Version = "2.31.0" }
            }
        };

        Assert.Equal("2.31.0", AppUpdateManifestParser.SelectLatest(manifest)?.Version);
    }

    [Fact]
    public void SelectLatestReturnsNullWhenNothingIsUsable()
    {
        Assert.Null(AppUpdateManifestParser.SelectLatest(null));
        Assert.Null(AppUpdateManifestParser.SelectLatest(new AppUpdateManifest()));

        var allGarbage = new AppUpdateManifest
        {
            Releases = new List<AppUpdateRelease> { new() { Version = "not-a-version" } }
        };
        Assert.Null(AppUpdateManifestParser.SelectLatest(allGarbage));
    }

    // 维护者从 GitHub tag 抄版本号时会带上 v，这不该让整条记录被跳过。
    [Fact]
    public void AVersionsLeadingVIsTolerated()
    {
        var manifest = AppUpdateManifestParser.Parse("""{ "releases": [ { "version": "v2.31.0" } ] }""");

        Assert.Equal("v2.31.0", AppUpdateManifestParser.SelectLatest(manifest)?.Version);
        Assert.True(AppUpdateManifestParser.TryParseVersion(" v2.31.0 ", out var version));
        Assert.Equal(new Version(2, 31, 0), version);
    }

    // ---- FindAsset ----------------------------------------------------------

    [Fact]
    public void FindAssetMatchesByKindFirst()
    {
        var latest = AppUpdateManifestParser.SelectLatest(AppUpdateManifestParser.Parse(ValidJson));

        var singleFile = AppUpdateManifestParser.FindAsset(latest, AppUpdateAsset.KindSingleFile,
            "SingleFile_JASM_");
        var folder = AppUpdateManifestParser.FindAsset(latest, AppUpdateAsset.KindFolder, "JASM_");

        Assert.Equal("SingleFile_JASM_v2.31.0.zip", singleFile?.Name);
        Assert.Equal("JASM_v2.31.0.7z", folder?.Name);
    }

    // kind 是显式字段，但手写清单时漏掉它很常见 —— 此时退回名字前缀，别让用户"更新不了"。
    [Fact]
    public void FindAssetFallsBackToTheNamePrefixWhenKindIsMissing()
    {
        var release = new AppUpdateRelease
        {
            Assets = new List<AppUpdateAsset>
            {
                new() { Name = "SingleFile_JASM_v2.31.0.zip", Url = "https://example.invalid/sf.zip" }
            }
        };

        var asset = AppUpdateManifestParser.FindAsset(release, AppUpdateAsset.KindSingleFile,
            "SingleFile_JASM_");

        Assert.Equal("SingleFile_JASM_v2.31.0.zip", asset?.Name);
    }

    // kind 与名字前缀都可能命中时，kind 更权威：万一名子写错，显式类型标记仍能挑对包。
    [Fact]
    public void KindWinsOverAPrefixMatchOnAnEarlierEntry()
    {
        var release = new AppUpdateRelease
        {
            Assets = new List<AppUpdateAsset>
            {
                new()
                {
                    Name = "SingleFile_JASM_v2.31.0.zip",
                    Kind = AppUpdateAsset.KindFolder,
                    Url = "https://example.invalid/mislabelled.zip"
                },
                new()
                {
                    Name = "jasm-2.31.0.zip",
                    Kind = AppUpdateAsset.KindSingleFile,
                    Url = "https://example.invalid/real.zip"
                }
            }
        };

        var asset = AppUpdateManifestParser.FindAsset(release, AppUpdateAsset.KindSingleFile,
            "SingleFile_JASM_");

        Assert.Equal("https://example.invalid/real.zip", asset?.Url);
    }

    // 写了条目却没有可用地址，等于没写：挑中它只会把失败推迟到下载那一步，错误信息还离根因很远。
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/app/SingleFile_JASM_v2.31.0.zip")] // 相对路径：清单里没有 base URL 可拼
    [InlineData("file:///C:/temp/x.zip")]
    public void FindAssetSkipsEntriesWithoutAnAbsoluteHttpUrl(string? url)
    {
        var release = new AppUpdateRelease
        {
            Assets = new List<AppUpdateAsset>
            {
                new() { Kind = AppUpdateAsset.KindSingleFile, Url = url },
                new()
                {
                    Kind = AppUpdateAsset.KindSingleFile,
                    Url = "https://example.invalid/ok.zip"
                }
            }
        };

        Assert.Equal("https://example.invalid/ok.zip",
            AppUpdateManifestParser.FindAsset(release, AppUpdateAsset.KindSingleFile, "SingleFile_JASM_")?.Url);
    }

    [Fact]
    public void FindAssetIsCaseInsensitiveOnKindAndPrefix()
    {
        var release = new AppUpdateRelease
        {
            Assets = new List<AppUpdateAsset>
            {
                new() { Kind = "SINGLEFILE", Url = "https://example.invalid/k.zip" }
            }
        };

        Assert.NotNull(AppUpdateManifestParser.FindAsset(release, AppUpdateAsset.KindSingleFile, "singleFile_"));

        var byPrefix = new AppUpdateRelease
        {
            Assets = new List<AppUpdateAsset>
            {
                new() { Name = "singlefile_jasm_2.31.0.zip", Url = "https://example.invalid/p.zip" }
            }
        };
        Assert.NotNull(AppUpdateManifestParser.FindAsset(byPrefix, AppUpdateAsset.KindSingleFile,
            "SingleFile_JASM_"));
    }

    [Fact]
    public void FindAssetReturnsNullForANullOrEmptyRelease()
    {
        Assert.Null(AppUpdateManifestParser.FindAsset(null, AppUpdateAsset.KindSingleFile, "SingleFile_JASM_"));
        Assert.Null(AppUpdateManifestParser.FindAsset(new AppUpdateRelease(), AppUpdateAsset.KindSingleFile,
            "SingleFile_JASM_"));
    }

    // ---- AppUpdateDownloadProgress ------------------------------------------

    [Fact]
    public void PercentIsComputedFromTheByteCounts()
    {
        Assert.Equal(50, new AppUpdateDownloadProgress(50, 100, 0).Percent);
        Assert.Equal(0, new AppUpdateDownloadProgress(0, 100, 0).Percent);
        Assert.Equal(100, new AppUpdateDownloadProgress(100, 100, 0).Percent);
    }

    // 服务器没给 Content-Length、清单也没写 sizeBytes 时总量是 0：此时必须停在 0 而不是
    // 除零或者假装走完 —— 调用方看到 0 该把进度条切成不确定态。
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PercentIsZeroWhenTheTotalIsUnknown(long total)
    {
        Assert.Equal(0, new AppUpdateDownloadProgress(12345, total, 0).Percent);
    }

    // 断点续传时"已收到"可能短暂超过总量（服务端的 Content-Length 与实际内容不一致），
    // 进度条不该因此冲出 100%。
    [Fact]
    public void PercentIsClampedTo100()
    {
        Assert.Equal(100, new AppUpdateDownloadProgress(200, 100, 0).Percent);
    }

    [Fact]
    public void DisplayTextShowsMegabytesAndSpeed()
    {
        var text = new AppUpdateDownloadProgress(45 * 1024 * 1024, 120 * 1024 * 1024, 1.2 * 1024 * 1024)
            .ToDisplayText();

        Assert.Equal("下载中 45.0 MB / 120.0 MB（1.2 MB/s）", text);
    }

    [Fact]
    public void DisplayTextSaysUnknownWhenTheTotalIsUnknown()
    {
        Assert.Equal("下载中 1.0 MB / 未知",
            new AppUpdateDownloadProgress(1024 * 1024, 0, 0).ToDisplayText());
    }

    [Fact]
    public void DisplayTextFallsBackToKilobytesPerSecondOnSlowLinks()
    {
        Assert.Equal("下载中 1.0 MB / 10.0 MB（500 KB/s）",
            new AppUpdateDownloadProgress(1024 * 1024, 10 * 1024 * 1024, 500 * 1024).ToDisplayText());
    }
}