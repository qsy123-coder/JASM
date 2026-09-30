using GIMI_ModManager.Core.Services.GameBanana;
using GIMI_ModManager.Core.Services.GameBanana.ApiModels;
using GIMI_ModManager.Core.Services.GameBanana.Models;

namespace GIMI_ModManager.Core.ModStore;

/// <summary>
/// 商店里一个 mod 的**一个可下载文件**（<c>Mod/{id}/DownloadPage</c> 的一条记录）。
///
/// 详情页要让用户自己挑文件（PRD 的决定：不自动挑），所以这一层的职责就是把「哪条能下、
/// 哪个是哪个版本」说清楚：<see cref="IsArchived"/> 标出来源数组、<see cref="Version"/>
/// 在文件级缺失时退回 mod 级。
/// </summary>
public sealed class ModStoreFile
{
    private ModStoreFile(ApiModFileInfo file, string? modVersion, bool fromArchivedList)
    {
        FileId = new GbModFileId(file.FileId);
        FileName = file.FileName.Trim();
        FileSize = file.FileSize >= 0 ? file.FileSize : null;

        // 文件级 _sVersion **经常缺**（实测 mod 575376 的 4 个文件一个都没有），
        // 缺了就退回 mod 级版本号 —— 否则「版本」这一列大半是空的，看着像没做。
        Version = NullIfBlank(file.Version) ?? NullIfBlank(modVersion);

        DownloadCount = file.DownloadCount >= 0 ? file.DownloadCount : null;
        DateAdded = ToDateTimeOffset(file.DateAdded);
        Description = GameBananaHtml.ToPlainText(file.Description);
        Md5Checksum = NullIfBlank(file.Md5Checksum);
        DownloadUrl = TryCreateDownloadUrl(file.DownloadUrl);

        // 标记与所在数组实测一致，但两边都看一遍：只认数组的话，
        // 哪天 GB 把归档文件混进 _aFiles，界面就不会提示「这是旧文件」。
        IsArchived = file.IsArchived || fromArchivedList;
    }

    /// <summary>
    /// 映射一条文件记录；下不了（没有 id）或显示不了（没有文件名）的返回 null。
    /// </summary>
    /// <param name="modVersion">
    /// mod 级版本号，作为文件级 <c>_sVersion</c> 缺失时的回退值。
    /// </param>
    /// <param name="fromArchivedList">这条是不是来自 <c>_aArchivedFiles</c>。</param>
    public static ModStoreFile? TryCreate(ApiModFileInfo? file, string? modVersion, bool fromArchivedList)
    {
        if (file is null || file.FileId <= 0)
            return null;

        // FileName 在 DTO 上声明为非空，但键缺失时**实际是 null** —— 别信声明，判一次。
        if (string.IsNullOrWhiteSpace(file.FileName))
            return null;

        return new ModStoreFile(file, modVersion, fromArchivedList);
    }

    public GbModFileId FileId { get; }

    public string FileName { get; }

    /// <summary>字节数；接口没给（&lt; 0）时是 null —— 界面少显示一列，而不是显示 -1。</summary>
    public long? FileSize { get; }

    /// <summary>文件级版本号，缺失时回退到 mod 级版本号；两处都没有时为 null。</summary>
    public string? Version { get; }

    public int? DownloadCount { get; }

    public DateTimeOffset? DateAdded { get; }

    /// <summary>文件说明，已洗成纯文本（作者常在这里写「这是更新包」「这是无图版」）。</summary>
    public string? Description { get; }

    /// <summary>
    /// 归档文件：被隐藏/归档的 mod 的文件全在这里（实测 mod 709792）。仍然可下，
    /// 但界面上要标出来 —— 用户有权知道自己在装一个不再展示的东西。
    /// </summary>
    public bool IsArchived { get; }

    public string? Md5Checksum { get; }

    /// <summary>下载地址；校验不过（非 https / 非 gamebanana.com）时为 null。</summary>
    public Uri? DownloadUrl { get; }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// 下载链接的校验口径与 <c>ModStoreMod</c> 校验 mod 页地址时一致：只认 https +
    /// gamebanana.com。**两份刻意不合并**（同 <c>GameBananaMediaUrls</c> 的说明）——
    /// 为一个商店页面去动市场侧已经跑通的代码，风险大于这点重复。
    ///
    /// ⚠️ 当前这一版**还没有下载功能**（PRD Phase 1 第 5/7 项），这里先把它校验好；
    /// 真正发请求前应再确认一次（<c>GameBananaCoreService.DownloadModAsync</c> 用的是
    /// 自己按文件 id 拼的 dl 地址，不走这个字段）。
    /// </summary>
    private static Uri? TryCreateDownloadUrl(string? downloadUrl)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl) || !Uri.TryCreate(downloadUrl, UriKind.Absolute, out var url))
            return null;

        // 必须判到「本域或子域」这一级：单纯 EndsWith("gamebanana.com") 会放行
        // evilgamebanana.com（那是攻击者能注册的域名）。
        if (url.Scheme != Uri.UriSchemeHttps || !IsGameBananaHost(url.Host))
            return null;

        return url;
    }

    private static bool IsGameBananaHost(string host) =>
        host.Equals("gamebanana.com", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".gamebanana.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Unix 秒 → <see cref="DateTimeOffset"/>；&lt;= 0 与超范围都按「没有」处理
    /// （<c>FromUnixTimeSeconds(0)</c> 会给出 1970-01-01 这种**看着合法的错日期**）。
    /// 与 <c>ModStoreMod</c> 里那份同口径。
    /// </summary>
    private static DateTimeOffset? ToDateTimeOffset(long unixSeconds)
    {
        if (unixSeconds <= 0 || unixSeconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }
}