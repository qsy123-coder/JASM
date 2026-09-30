using GIMI_ModManager.Core.ModStore;
using GIMI_ModManager.Core.Services.GameBanana;
using GIMI_ModManager.Core.Services.GameBanana.Models;

namespace GIMI_ModManager.Core.Services.Downloading;

/// <summary>
/// 下载任务的唯一标识：一个 mod 的**一个文件**。
///
/// 用「mod + 文件」而不是只用文件 id：GameBanana 的文件 id 确实全局唯一，但队列要能回答
/// 「这个 mod 的哪个文件在队里」——界面上是按 mod 展示的。两段都是字符串，
/// 与 <see cref="GbModId"/> / <see cref="GbModFileId"/> 保持一致（它们本身就是字符串包装）。
/// </summary>
public readonly record struct ModDownloadKey(string ModId, string ModFileId)
{
    public override string ToString() => $"{ModId}/{ModFileId}";
}

/// <summary>
/// 一次入队请求。<b>不可变</b> —— 队列拿到它就只读，之后不再依赖调用方那边的东西
/// （商店的列表/详情随时可能被刷新替换掉）。
/// </summary>
/// <param name="Key">任务标识。</param>
/// <param name="DownloadUrl">下载地址。</param>
/// <param name="FileName">文件名（展示用；落盘时会再净化一次）。</param>
/// <param name="ExpectedMd5">期望的 MD5（GameBanana 的 <c>_sMd5Checksum</c>）；为空则跳过校验。</param>
/// <param name="FileSizeBytes">声明的字节数；只用于进度分母和识破陈旧的 <c>.part</c>。</param>
/// <param name="ModName">mod 名（面板里显示，不影响下载）。</param>
/// <param name="Version">版本号（面板里显示）。</param>
/// <param name="Character">
/// 目标角色（商店给的是 GameBanana 的子分类名，如 <c>Jinhsi</c>）；UI 类 mod 没有，为 null。
/// **下载不用它**，是给下载完之后的部署阶段用的 —— 队列的契约是「拿到请求就自足」，
/// 所以「下完装哪儿」也得跟着请求走，不能留一张界面侧的表等着查。
/// </param>
/// <param name="ModPageUrl">
/// mod 页面地址。部署时当安装向导的 <c>ModUrl</c> 传进去 —— JASM 靠它把本地 mod 与
/// GameBanana 上的条目对上（「可更新」提示就是这么认的），漏了它装出来的 mod 会变成无主的。
/// </param>
public sealed record ModDownloadRequest(
    ModDownloadKey Key,
    Uri DownloadUrl,
    string FileName,
    string? ExpectedMd5 = null,
    long? FileSizeBytes = null,
    string? ModName = null,
    string? Version = null,
    string? Character = null,
    Uri? ModPageUrl = null)
{
    /// <summary>
    /// 把商店详情里的一个文件转成下载请求。
    ///
    /// 地址优先用上游给的 <c>_sDownloadUrl</c>（已按 https + gamebanana.com 校验过），
    /// 缺失时退回按文件 id 拼 <c>https://gamebanana.com/dl/{fileId}</c> ——
    /// 这不是猜的：<c>ApiGameBananaClient.DownloadModAsync</c> 一直就是这么下的，实测
    /// 302 两次后回 206，<c>Range</c> 续传可用。
    /// </summary>
    /// <param name="modName">mod 名（面板显示）。</param>
    /// <param name="character">目标角色（GameBanana 子分类名）；UI 类 mod 传 null。</param>
    /// <param name="modPageUrl">mod 页面地址；部署时当安装向导的 <c>ModUrl</c>。</param>
    /// <returns>请求；文件既没有地址也拼不出地址时返回 null（调用方按「这个文件下不了」处理——
    /// 正常路径下不会发生，是防御性的）。</returns>
    public static ModDownloadRequest? FromStoreFile(GbModId modId, ModStoreFile file, string? modName,
        string? character = null, Uri? modPageUrl = null)
    {
        ArgumentNullException.ThrowIfNull(modId);
        ArgumentNullException.ThrowIfNull(file);

        var url = file.DownloadUrl ?? TryBuildDownloadUrl(file.FileId);
        if (url is null)
            return null;

        return new ModDownloadRequest(
            new ModDownloadKey(modId.ModId, file.FileId.ModFileId),
            url,
            file.FileName,
            file.Md5Checksum,
            file.FileSize,
            modName,
            // ModStoreFile.Version 已经把「文件级缺失就退回 mod 级」做完了，这里直接用。
            file.Version,
            character,
            modPageUrl);
    }

    private static Uri? TryBuildDownloadUrl(GbModFileId fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId.ModFileId))
            return null;

        return Uri.TryCreate(ApiGameBananaClient.DownloadUrlPrefix + fileId.ModFileId, UriKind.Absolute,
            out var url)
            ? url
            : null;
    }
}

/// <summary>队列任务的状态。终态是 <see cref="Completed"/> 与 <see cref="Failed"/>（<see cref="Paused"/> 还能继续）。</summary>
public enum ModDownloadState
{
    /// <summary>排队中，等前面的任务让出位置。</summary>
    Queued,

    /// <summary>正在接收数据。</summary>
    Downloading,

    /// <summary>数据收完，正在校验整文件哈希（这一步没有字节在动，进度条要单独说一句）。</summary>
    Verifying,

    /// <summary>用户暂停（或失败后暂停）。<c>.part</c> 保留，继续时从断点接着传。</summary>
    Paused,

    /// <summary>下载 + 校验都过了，文件已在暂存目录。</summary>
    Completed,

    /// <summary>失败；<see cref="ModDownloadItem.ErrorMessage"/> 里有给人看的原因，可「继续」重试。</summary>
    Failed
}

/// <summary>
/// 队列里的一个下载任务。**可变**且会被工作线程改，所以界面侧读到的是快照 ——
/// 真正的通知走 <see cref="ModDownloadQueue.Changed"/>（在那个事件里刷新整行）。
///
/// 身份字段（Key / 文件名 / 地址 / 哈希 / 声明体积）是只读的；状态字段由队列写。
/// </summary>
public sealed class ModDownloadItem
{
    internal ModDownloadItem(ModDownloadRequest request, string destinationPath)
    {
        Key = request.Key;
        DownloadUrl = request.DownloadUrl;
        FileName = request.FileName;
        ExpectedMd5 = request.ExpectedMd5;
        FileSizeBytes = request.FileSizeBytes;
        ModName = request.ModName;
        Version = request.Version;
        Character = request.Character;
        ModPageUrl = request.ModPageUrl;
        DestinationPath = destinationPath;

        // 声明体积先垫上：这样进度条在第一份数据到达之前就有分母（否则会先显示一段「未知进度」）。
        TotalBytes = request.FileSizeBytes;
    }

    public ModDownloadKey Key { get; }

    public Uri DownloadUrl { get; }

    public string FileName { get; }

    public string? ExpectedMd5 { get; }

    public long? FileSizeBytes { get; }

    /// <summary>mod 名；上游没给（比如从列表直接部署、没开详情）时为 null。</summary>
    public string? ModName { get; }

    public string? Version { get; }

    /// <summary>目标角色（商店的 GameBanana 子分类名）；UI 类 mod 为 null —— 部署时退回「Others」。</summary>
    public string? Character { get; }

    /// <summary>mod 页面地址；部署时当安装向导的 <c>ModUrl</c>。</summary>
    public Uri? ModPageUrl { get; }

    /// <summary>落盘位置（暂存目录里的完整路径）。</summary>
    public string DestinationPath { get; }

    /// <summary>续传文件路径（<c>&lt;目标&gt;.part</c>）。</summary>
    public string PartPath => ResumableDownloader.GetPartPath(DestinationPath);

    public ModDownloadState State { get; internal set; } = ModDownloadState.Queued;

    public long BytesReceived { get; internal set; }

    /// <summary>总字节数；声明和响应都没给时为 null —— 那时界面不显示百分比。</summary>
    public long? TotalBytes { get; internal set; }

    public double BytesPerSecond { get; internal set; }

    /// <summary>给人看的失败原因（中文，可直接显示）；成功时为 null。</summary>
    public string? ErrorMessage { get; internal set; }

    /// <summary>失败分类，界面据此决定要不要给「继续」按钮（哈希不符重试同一份数据没意义，但换文件有意义）。</summary>
    public DownloadFailureReason? FailureReason { get; internal set; }

    /// <summary>
    /// 下载本身成功、但后续动作（归档入库 / 拉起安装向导）失败的原因。
    /// 与 <see cref="ErrorMessage"/> 分开：文件是好的，不该显示成下载失败。
    /// </summary>
    public string? FollowUpError { get; internal set; }

    /// <summary>百分比；总长未知时为 null（与 <see cref="DownloadProgress.Percent"/> 同义）。</summary>
    public int? Percent => TotalBytes is > 0
        ? (int)Math.Clamp(BytesReceived * 100 / TotalBytes.Value, 0, 100)
        : null;

    /// <summary>正在跑（占着队列的唯一位置）。</summary>
    public bool IsActive => State is ModDownloadState.Downloading or ModDownloadState.Verifying;

    /// <summary>跑完了：成功或失败，都还在等用户处理。</summary>
    public bool IsFinished => State is ModDownloadState.Completed or ModDownloadState.Failed;

    /// <summary>用户要求暂停（它可能还没轮到跑）。</summary>
    internal bool PauseRequested { get; set; }

    /// <summary>用户要求取消。取消的任务会立刻从队列里消失，这里只用来让工作线程知道该删 <c>.part</c> 还是留着。</summary>
    internal bool CancelRequested { get; set; }

    public override string ToString() => $"{Key} {FileName} [{State}]";
}