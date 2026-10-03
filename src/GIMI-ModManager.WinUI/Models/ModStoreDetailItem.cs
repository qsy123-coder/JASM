using CommunityToolkit.Mvvm.ComponentModel;
using GIMI_ModManager.Core.ModStore;

namespace GIMI_ModManager.WinUI.Models;

/// <summary>
/// 详情抽屉的数据源（<c>ModStoreDetailPanel</c> 的 DataContext）。
///
/// 为什么是「先建后补」：点卡片时列表记录**已经**带着标题/作者/三项统计/预览图，
/// 详情接口（两个请求）回来要几百毫秒。所以先在 <see cref="FromCard"/> 用现有数据把抽屉画出来，
/// 详情回来再 <see cref="ApplyDetail"/> 就地覆盖 —— 用户看到的是「已经打开、正在补内容」，
/// 而不是「点了没反应」。
///
/// 因此它是 <see cref="ObservableObject"/> 且属性要能**二次赋值**：这跟
/// <see cref="ModStoreItem"/>（卡片，建完就不变）的形态刻意不同。
/// 界面上每个值都要想清楚「列表给的和详情给的哪个更可信」——见 <see cref="ApplyDetail"/>。
/// </summary>
public partial class ModStoreDetailItem : ObservableObject
{
    private ModStoreDetailItem(ModStoreItem card)
    {
        GbModId = card.GbModId;
        _title = card.Title;
        _authorName = card.AuthorName;
        _character = card.Character;
        _viewsCount = card.ViewsCount;
        _likesCount = card.LikesCount;
        _commentsCount = card.CommentsCount;
        _updatedAt = card.UpdatedAt;
        _isInstalled = card.IsInstalled;
        ModPageUrl = card.ModPageUrl;
        PreviewImageUrl = card.PreviewImageUrl;
    }

    /// <summary>用卡片上已有的字段开一个抽屉，随后由 <see cref="ApplyDetail"/> 补详情。</summary>
    public static ModStoreDetailItem FromCard(ModStoreItem card) => new(card);

    /// <summary>GameBanana mod id —— 补详情、下载都靠它。</summary>
    public string GbModId { get; }

    // ─── 列表记录就有的 ────────────────────────────────────────

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private string? _authorName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCharacter))]
    private string? _character;

    [ObservableProperty]
    private int? _viewsCount;

    [ObservableProperty]
    private int? _likesCount;

    [ObservableProperty]
    private int? _commentsCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RelativeTime))]
    private DateTimeOffset? _updatedAt;

    /// <summary>mod 页面地址（卡片上就校验过），详情抽屉的「查看原页面」用。</summary>
    public Uri? ModPageUrl { get; }

    /// <summary>列表里的预览图；详情回来前先用它撑住画面。</summary>
    public string? PreviewImageUrl { get; }

    // ─── 安装状态 ──────────────────────────────────────────────

    /// <summary>
    /// 「已安装」角标。先跟着卡片走（同一次列表加载里判的），详情回来之后由
    /// <see cref="ApplyInstallStatus"/> 再确认一次 —— 那时才拿到文件清单，也就才判得了「可更新」。
    /// </summary>
    [ObservableProperty]
    private bool _isInstalled;

    /// <summary>
    /// 「可更新」角标（PRD Story 4）。**只有详情里判得了**：判定要文件清单（file id / md5），
    /// 而列表记录里根本没有文件信息 —— 给每张卡都补一次 DownloadPage 请求，一屏十几张就是十几个请求，
    /// 代价与收益不成比例，所以卡片上不打这个角标（见 docs/mod-store-prd.md 第 8 项）。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotUpdatable))]
    private bool _hasUpdate;

    /// <summary>「不是可更新」。下载按钮上那两句话（「下载选中文件」/「更新到最新版本」）靠它二选一。</summary>
    public bool IsNotUpdatable => !HasUpdate;

    // ─── 详情回来才有的 ────────────────────────────────────────

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>非空 = 这次详情没取到（界面显示原因 + 重试）。成功时必须是 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAuthorAvatar))]
    private Uri? _authorAvatarUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    private string? _description;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBody))]
    [NotifyPropertyChangedFor(nameof(HasNoBody))]
    private string? _body;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionText))]
    private string? _version;

    [ObservableProperty]
    private int? _downloadsCount;

    [ObservableProperty]
    private int? _thanksCount;

    [ObservableProperty]
    private bool _isObsolete;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatesText))]
    private bool _hasUpdates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatesText))]
    private int? _updatesCount;

    [ObservableProperty]
    private DateTimeOffset? _dateAdded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImages))]
    private IReadOnlyList<Uri> _images = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFiles))]
    [NotifyPropertyChangedFor(nameof(HasNoFiles))]
    private IReadOnlyList<ModStoreFileItem> _files = [];

    /// <summary>
    /// 用户点选的文件。PRD 要求多文件时**让用户自己挑**，所以这里只是记住选择 ——
    /// 下载/部署发生在下一个增量，读的就是这个属性。
    /// </summary>
    [ObservableProperty]
    private ModStoreFileItem? _selectedFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRatings))]
    [NotifyPropertyChangedFor(nameof(NsfwLabels))]
    private IReadOnlyList<string> _contentRatings = [];

    // ─── 显示用计算属性 ────────────────────────────────────────

    public bool HasCharacter => !string.IsNullOrWhiteSpace(Character);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool HasAuthorAvatar => AuthorAvatarUrl is not null;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool HasBody => !string.IsNullOrWhiteSpace(Body);

    /// <summary>正文和简介都没有 —— 图文描述那一页要说清楚是「作者没写」，不是加载失败。</summary>
    public bool HasNoBody => !HasBody;

    public bool HasImages => Images.Count > 0;

    public bool HasFiles => Files.Count > 0;

    public bool HasNoFiles => !HasFiles;

    public bool HasRatings => ContentRatings.Count > 0;

    /// <summary>「更新过 50 次」；作者没标记更新时是空串（那一格不显示）。</summary>
    public string UpdatesText => !HasUpdates
        ? string.Empty
        : UpdatesCount is { } count and > 0
            ? $"更新过 {count} 次"
            : "有更新";

    /// <summary>内容分级标签（如 <c>Partial Nudity</c>）。GameBanana 给的就是英文原文，不翻译。</summary>
    public string NsfwLabels => string.Join(" · ", ContentRatings);

    public string VersionText => string.IsNullOrWhiteSpace(Version) ? string.Empty : $"v{Version}";

    /// <summary>与卡片同一套相对时间口径（<see cref="ModStoreItem.GetRelativeTime"/>）。</summary>
    public string RelativeTime => UpdatedAt is { } time ? ModStoreItem.GetRelativeTime(time) : string.Empty;

    public string UpdatedText =>
        UpdatedAt is { } time ? $"更新于 {time.ToLocalTime():yyyy-MM-dd}" : string.Empty;

    public string AddedText => DateAdded is { } time ? $"发布于 {time.ToLocalTime():yyyy-MM-dd}" : string.Empty;

    /// <summary>
    /// 把详情就地覆盖到已有字段上。
    ///
    /// 每条都按「**详情更可信，但缺值不要抹掉列表已经有的**」处理：两个端点的统计口径一致，
    /// 可详情偶尔不给某一项（如 <c>_nLikeCount</c>），这时保留列表的值比显示空好 ——
    /// 但绝不用列表的值去覆盖详情给的真值（下载量就只在详情有，列表恒为 null）。
    /// </summary>
    public void ApplyDetail(ModStoreDetail detail)
    {
        if (!string.IsNullOrWhiteSpace(detail.Name))
            Title = detail.Name;

        AuthorName = detail.AuthorName ?? AuthorName;
        Character = detail.Character ?? Character;
        AuthorAvatarUrl = detail.AuthorAvatarUrl;

        Description = detail.Description;
        Body = detail.Body;

        Version = detail.Version;

        ViewsCount = detail.ViewCount ?? ViewsCount;
        LikesCount = detail.LikeCount ?? LikesCount;
        CommentsCount = detail.CommentCount ?? CommentsCount;
        DownloadsCount = detail.DownloadCount;
        ThanksCount = detail.ThanksCount;

        UpdatedAt = detail.DateUpdated ?? UpdatedAt;
        DateAdded = detail.DateAdded;

        IsObsolete = detail.IsObsolete;
        HasUpdates = detail.HasUpdates;
        UpdatesCount = detail.UpdatesCount;

        ContentRatings = detail.ContentRatings;
        Images = detail.PreviewImages;

        var files = detail.Files.Select(ModStoreFileItem.FromFile).ToArray();
        Files = files;

        // 默认选中第一个「活跃」文件（归档的排在清单后面）。用户仍然可以改选 ——
        // 这里只是不想让「还没选任何文件」成为初始状态（那样下一步的部署按钮看起来是坏的）。
        SelectedFile = files.FirstOrDefault(file => !file.IsArchived) ?? files.FirstOrDefault();
    }

    /// <summary>
    /// 详情回来之后补上安装状态（角标 + 按钮文案）。判定本身在 Core 的
    /// <see cref="ModStoreInstallStatus"/>，这里只负责把结果落到界面。
    /// </summary>
    /// <param name="installed">页面判出来的「现在还装着吗」（要查磁盘 + 本地 mod 列表，不是这里能回答的）。</param>
    /// <param name="record">安装记录；没装过时为 null。</param>
    public void ApplyInstallStatus(bool installed, ModStoreInstallRecord? record)
    {
        IsInstalled = installed;

        var files = Files.Select(file => file.Source).ToArray();
        HasUpdate = installed && ModStoreInstallStatus.HasUpdate(record, files);

        if (!HasUpdate)
            return;

        // 角标说「有新版本」，而默认选中的还是清单里第一个活跃文件 —— 用户直接点「更新」就会装上
        // 自己已经装过的那一份。预选必须跟着**同一个判定**走（FindLatestFile）。
        if (ModStoreInstallStatus.FindLatestFile(files) is { } latest)
            SelectedFile = Files.FirstOrDefault(file => file.FileId == latest.FileId.ToString()) ?? SelectedFile;
    }
}