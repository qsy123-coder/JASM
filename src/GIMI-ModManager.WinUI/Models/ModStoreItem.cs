using CommunityToolkit.Mvvm.ComponentModel;
using GIMI_ModManager.Core.ModStore;

namespace GIMI_ModManager.WinUI.Models;

/// <summary>
/// 商店卡片（以及后续详情抽屉）用的 UI 模型，由 Core 的 <see cref="ModStoreMod"/> 映射而来。
///
/// 为什么不复用市场侧的 <c>ModMarketMod</c>：那个是 Supabase 表行（snake_case 列 + 网盘直链），
/// 这个是 GameBanana 列表记录（Unix 秒时间戳 + 分类对象 + 没有下载量），来源与形状都不同 ——
/// 合成一个类就得在每个属性上注明「哪种来源时它有值」，两个来源会互相牵制。
///
/// 映射出来的字段**建完就不变**（一条 GameBanana 记录就是那样），卡片在代码里手工搭出来、
/// 绑定也是一次性的；唯一的例外是 <see cref="IsInstalled"/> —— 它不属于列表记录，而是本地状态，
/// 装完之后可以不刷新列表就变，所以只有它是可观察的。
/// </summary>
public sealed partial class ModStoreItem : ObservableObject
{
    /// <summary>GameBanana mod id —— 详情页、文件列表、下载都靠它。</summary>
    public string GbModId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? AuthorName { get; init; }

    /// <summary>
    /// 角色名，来自 <c>_aSubCategory</c>。
    /// ⚠️ 不是每条记录都有：UI 类 mod 就没有角色（实测），所以可空，卡片上别硬占位。
    /// </summary>
    public string? Character { get; init; }

    /// <summary>
    /// 卡片上那块角色标签显示不显示。给 XAML 绑定用（<c>BoolToVisibilityConverter</c> 只吃 bool，
    /// 字符串非空判断放这儿比再写一个转换器省事）。
    /// </summary>
    public bool HasCharacter => !string.IsNullOrWhiteSpace(Character);

    /// <summary>根分类名：<c>Skins</c> / <c>UI</c> / <c>Other-Misc</c>（鸣潮板块只有这三个）。</summary>
    public string? RootCategory { get; init; }

    public string? Version { get; init; }

    public int? LikesCount { get; init; }

    public int? ViewsCount { get; init; }

    /// <summary>
    /// 评论数（GameBanana 的 <c>_nPostCount</c>）。三个列表端点都给，所以卡片上的第三项统计
    /// 可以稳定显示 —— 不像下载量只有详情页才有。
    /// </summary>
    public int? CommentsCount { get; init; }

    /// <summary>
    /// 成人内容（列表里的 <c>_bHasContentRatings</c>）。卡片上只用它显示角标 ——
    /// 这类条目默认在 <c>ModStoreService</c> 就被丢掉了（<c>IncludeAdultContent</c> = false），
    /// 开关打开后才会出现在列表里。
    /// </summary>
    public bool IsAdult { get; init; }

    public string? PreviewImageUrl { get; init; }

    public Uri? ModPageUrl { get; init; }

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// 「已安装」角标（PRD Story 4）。**故意不放进 <see cref="FromMod"/>**：那个映射只认 GameBanana
    /// 给的列表记录，而「装没装过」是本地的事（安装记录 + 那份 mod 目录还在不在，见
    /// <see cref="ModStoreInstallStatus"/>）—— 页面取完数、把卡片加进列表之前判一次填上。
    ///
    /// 可观察是为了**装完立刻显示**：向导是独立窗口，装完时这一页还停在原地，
    /// 部署服务的 <c>InstallRecorded</c> 会让页面把这一列重判一遍（见 ModStoreViewModel）。
    /// </summary>
    [ObservableProperty]
    private bool _isInstalled;

    /// <summary>相对时间文案；没有时间戳时是空串（卡片上就什么都不显示）。</summary>
    public string RelativeTime => UpdatedAt is { } time ? GetRelativeTime(time) : string.Empty;

    public static ModStoreItem FromMod(ModStoreMod mod)
    {
        return new ModStoreItem
        {
            GbModId = mod.Id.ModId,
            Title = mod.Name,
            AuthorName = mod.AuthorName,
            Character = mod.Character,
            RootCategory = mod.Category?.Name,
            Version = mod.Version,
            LikesCount = mod.LikeCount,
            ViewsCount = mod.ViewCount,
            CommentsCount = mod.CommentCount,
            IsAdult = mod.IsAdult,
            PreviewImageUrl = mod.PreviewImages.Count > 0 ? mod.PreviewImages[0].ToString() : null,
            ModPageUrl = mod.ModPageUrl,
            UpdatedAt = mod.DateUpdated ?? mod.DateAdded
        };
    }

    /// <summary>
    /// 与市场侧同一套文案口径（那边是 ModMarketMod.GetRelativeTime）—— 两处刻意保持一致的措辞。
    /// <c>internal</c> 而非 private：详情抽屉（<c>ModStoreDetailItem</c>）要复用同一套口径，
    /// 卡片写「3天前」、抽屉写「2026-03-11」这种不一致最容易让人怀疑数据错了。
    /// </summary>
    internal static string GetRelativeTime(DateTimeOffset time)
    {
        var span = DateTimeOffset.UtcNow - time;

        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}分钟前";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}小时前";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}天前";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}个月前";
        return $"{(int)(span.TotalDays / 365)}年前";
    }
}