// 显式 using（不靠 ImplicitUsings）：本文件会被 JASM.AutoUpdater 以「源链接」方式编译进去
// （见 JASM.AutoUpdater.csproj），而那个工程没开 ImplicitUsings。同 Core/Helpers/KeyHelperProtocol.cs。
using System;
using System.Collections.Generic;

namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// JASM 应用更新清单 —— 对应 COS 上 <c>app/update.json</c>。
///
/// **为什么需要它**：更新包以前挂在 GitHub Releases 上，"最新版本是哪个、包在哪" 全靠 GitHub API
/// 的 <c>releases</c> 数组顺带给出。搬到国内可直连的 COS 之后，对象存储只提供文件本身、不提供
/// 元数据，所以那份元数据得自己带 —— 就是本清单，包本体放在同桶的 <c>app/</c> 前缀下。
///
/// 字段一律可空 / 可缺省：清单是远端数据，维护者手改也可能出错。缺字段只应让**那一条**被跳过，
/// 绝不允许把客户端打挂（解析与筛选见 <see cref="AppUpdateManifestParser"/>）。
/// </summary>
public sealed class AppUpdateManifest
{
    /// <summary>清单格式版本。当前只认 1，将来改结构时用它判别。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// 发布记录，按版本号**降序**排列。客户端只取其中最大的非预发布版本 —— 这与以前
    /// "过滤掉 GitHub 的 prerelease 再取 Max" 的语义一致，用户看到的仍是同一个"最新版"。
    /// </summary>
    public List<AppUpdateRelease> Releases { get; set; } = new();
}

/// <summary>清单里的一条发布记录，对应以前 GitHub 的一次 release。</summary>
public sealed class AppUpdateRelease
{
    /// <summary>
    /// 版本号，形如 <c>2.31.0</c>。**不带** <c>v</c> 前缀（GitHub 的 tag 才带，客户端以前是靠
    /// <c>Trim('v')</c> 补上这一步的）。必须能被 <c>Version.TryParse</c> 解析出来。
    /// </summary>
    public string? Version { get; set; }

    /// <summary>预发布版本。为 <c>true</c> 时普通用户不会被提示更新（与 GitHub 的 prerelease 同义）。</summary>
    public bool Prerelease { get; set; }

    public DateTime PublishedAt { get; set; }

    /// <summary>
    /// "本次更新了什么" 的可读链接（通常仍指向 GitHub 的 release 页）。纯展示用，客户端不解析它，
    /// 缺省时退回仓库的 releases 列表页。
    /// </summary>
    public string? NotesUrl { get; set; }

    public List<AppUpdateAsset> Assets { get; set; } = new();
}

/// <summary>一条发布记录里的一个可下载产物。</summary>
public sealed class AppUpdateAsset
{
    /// <summary>folder 版（.7z，包内含独立的 <c>JASM - Auto Updater.exe</c>，走外部更新器通道）。</summary>
    public const string KindFolder = "folder";

    /// <summary>单文件版（.zip，只有一个 exe，走进程内自更新通道）。</summary>
    public const string KindSingleFile = "singleFile";

    /// <summary>资产文件名（如 <c>SingleFile_JASM_v2.31.0.zip</c>）。作展示，也作 <see cref="Kind"/> 缺失时的回退匹配。</summary>
    public string? Name { get; set; }

    /// <summary>
    /// 资产类型，取值 <see cref="KindFolder"/> 或 <see cref="KindSingleFile"/>。
    ///
    /// 显式判别比嗅探文件名可靠：单文件包叫 <c>SingleFile_JASM_*.zip</c>、folder 包叫
    /// <c>JASM_*.7z</c>，两者靠前缀区分，改个命名就会挑错包。缺省时调用方退回名字前缀匹配，
    /// 这样维护者手写清单时少写一个字段也不会挑不到包。
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>COS 上的直链。为空 / 非绝对 URL 时该条视为不可用。</summary>
    public string? Url { get; set; }

    /// <summary>字节数。<c>0</c> = 未知（下载进度的总量来源之一，见 <see cref="AppUpdateDownloadProgress"/>）。</summary>
    public long SizeBytes { get; set; }

    /// <summary>SHA256（小写十六进制）。空 = 不做完整性校验。</summary>
    public string? Sha256 { get; set; }
}