using CommunityToolkit.Mvvm.ComponentModel;
using GIMI_ModManager.Core.ModStore;

namespace GIMI_ModManager.WinUI.Models;

/// <summary>侧栏一项的身份，决定「点它以后怎么取数」。</summary>
public enum ModStoreSidebarKind
{
    /// <summary>不筛任何东西，走板块内容流 —— 唯一能用排序的一档。</summary>
    All,

    /// <summary>板块根分类（Skins / Other-Misc / UI），按 id 走服务端分类筛选。</summary>
    RootCategory,

    /// <summary>角色，按名字走搜索端点。</summary>
    Character
}

/// <summary>
/// 「Mod 商店」左侧栏的一项 —— 「全部」/ 板块根分类 / 角色三种身份共用一个类型。
///
/// 为什么不分三个集合：界面上它们本来就是**一条平铺列表**（照搬 Mod 市场的样子），
/// 拆成三段会让 XAML 变成三块拼接的 ListView，选中态还得自己对齐。
///
/// 两个筛选字段按身份二选一：
/// <list type="bullet">
///   <item><see cref="GbName"/> 只有角色有值（GameBanana 子分类名，既当搜索关键词又当客户端比对值）；</item>
///   <item><see cref="RootCategoryId"/> 只有根分类有值；</item>
///   <item>「全部」两个都是 null —— 用 null 而不是空串，空串会和「名字没解析出来」撞在一起。</item>
/// </list>
/// </summary>
public partial class ModStoreSidebarItem : ObservableObject
{
    /// <summary>文件夹字形（分类与「全部」共用）。</summary>
    private const string FolderGlyph = "\uE8B7";

    /// <summary>人物字形（角色）。</summary>
    private const string CharacterGlyph = "\uE77B";

    public ModStoreSidebarKind Kind { get; init; }

    public string? GbName { get; init; }

    public int? RootCategoryId { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>左侧的兜底字形（Segoe Fluent）—— 只有在没有 GameBanana 真图标时才显示。</summary>
    public string Glyph { get; init; } = FolderGlyph;

    /// <summary>
    /// GameBanana 的分类 / 角色图标（根分类来自板块主页，角色来自搜索记录）。null = 没有，
    /// 界面显示 <see cref="Glyph"/> 兜底 —— 不是留一格空白。
    ///
    /// 可变：角色图标与条目数来自同一次搜索请求，是后台一个个补进来的
    /// （见 <c>ModStoreViewModel.FillCountsAsync</c>），所以它跟着 <see cref="ItemCount"/> 一起晚到。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIcon))]
    private Uri? _iconUrl;

    /// <summary>有没有真图。给 XAML 的 <c>BoolToVisibilityConverter</c> 用（判空逻辑放这儿，省一个转换器）。</summary>
    public bool HasIcon => IconUrl is not null;

    /// <summary>
    /// 条目数。<b>null = 还不知道 / 没取到</b>（界面上就不显示数字），0 是真实结果，两者别混。
    ///
    /// 之所以可变而不是 <c>init</c>：侧栏五十多个角色，逐个问接口很慢，所以先渲染名字、
    /// 计数在后台一个个补进来（见 <c>ModStoreViewModel.FillCountsAsync</c>）。
    /// </summary>
    [ObservableProperty]
    private int? _itemCount;

    /// <summary>「全部」。做成工厂方法而不是静态单例：计数是可变状态，别让多个页面实例共享同一份。</summary>
    public static ModStoreSidebarItem CreateAll() => new()
    {
        Kind = ModStoreSidebarKind.All,
        DisplayName = "全部"
    };

    public static ModStoreSidebarItem FromRootCategory(ModStoreRootCategory category) => new()
    {
        Kind = ModStoreSidebarKind.RootCategory,
        RootCategoryId = category.Id,
        DisplayName = category.Name,
        // 接口没给条目数时是负数（见 ModStoreRootCategory.ItemCount），按「未知」处理。
        ItemCount = category.ItemCount >= 0 ? category.ItemCount : null,
        // 根分类的图标随板块主页一次取回，是同步就有的（角色图标才需要等后台补）。
        IconUrl = category.IconUrl
    };

    public static ModStoreSidebarItem FromCharacter(string gbName, string displayName) => new()
    {
        Kind = ModStoreSidebarKind.Character,
        GbName = gbName,
        DisplayName = displayName,
        Glyph = CharacterGlyph
    };
}