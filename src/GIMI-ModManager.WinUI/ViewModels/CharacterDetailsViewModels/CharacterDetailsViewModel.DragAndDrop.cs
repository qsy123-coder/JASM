using Windows.Storage;
using GIMI_ModManager.Core.Helpers;
using GIMI_ModManager.Core.Services.GameBanana;

namespace GIMI_ModManager.WinUI.ViewModels.CharacterDetailsViewModels;

public partial class CharacterDetailsViewModel
{
    /// <summary>
    /// 拖进来能当 Mod 收的文件扩展名。
    ///
    /// <c>.exe</c> 在列是因为社区大量 Mod 以自解压包（WinRAR SFX）分发。放行它不会把随便一个程序收进来：
    /// 解压器先按文件头确认这个 exe 真的追加了归档（<c>SfxPayloadDetector</c>），再让内置 7-Zip 当压缩包读 ——
    /// 只问「7-Zip 能不能打开」是不行的，实测它对普通 PE 也「成功」，抽出来的是 .text/.rdata 这些节。
    /// 详情页与浮窗、概览页走的是同一个解压器，所以这里只是把那张白名单补齐，不需要另开一条管线。
    /// </summary>
    private static readonly string[] AcceptedDropFileExtensions = [.. Constants.SupportedArchiveTypes, ".exe"];

    /// <summary>
    /// 这一拖收不收。收不下时用 <see cref="DescribeDragDropRefusal"/> 取给用户看的那句话。
    ///
    /// 判定与说法只有这一份：光标显不显示禁止（DragEnter）与松手之后走不走（Drop）必须是同一个结论，
    /// 两处各自判断就会出现「光标说收、松手没反应」这类不报错的偏差。
    /// </summary>
    public bool CanDragDropMod(IReadOnlyList<IStorageItem>? items) => DescribeDragDropRefusal(items) is null;

    /// <summary>
    /// 收不下就返回给用户看的那句话，收得下返回 <c>null</c>。
    ///
    /// <para>
    /// <b>为什么每条拒绝都得有句话</b>：XAML 的拖放不接受就是不设 <c>AcceptedOperation</c>，
    /// 用户那边只看到禁止光标 —— 松手连 Drop 事件都不会来，界面上一个字都没有。
    /// 于是「拖两个包」「拖了个 exe」「JASM 正忙」这三种完全不同的来路，
    /// 在用户眼里长得一模一样，只能来问「为什么拖不进去」。这里把理由说出来，它才是可回答的。
    /// </para>
    /// </summary>
    public string? DescribeDragDropRefusal(IReadOnlyList<IStorageItem>? items)
    {
        if (IsHardBusy)
            return "JASM 正忙（还有安装或移动任务在跑），等它做完再拖。";

        if (items is null || items.Count == 0)
            return "拖进来的东西里没有文件。如果你是从网盘客户端或压缩软件的窗口里直接拖的，"
                   + "请先把文件存到文件夹，再从那里拖过来。";

        // 一次只收一个：真正装的那条路（ModDragAndDropService.AddStorageItemFoldersAsync）
        // 自己就只吃一个 storage item，概览页的自动识别区也是同一条限制。能成批装的只有游戏内浮窗。
        if (items.Count > 1)
            return $"一次只能拖一个（你拖了 {items.Count} 个）。要一次装多个包，请拖到游戏内浮窗上。";

        var item = items[0];

        if (IsFolder(item))
            return null;

        // 虚拟文件（网盘占位、压缩软件窗口里的条目）拿得到路径、读不到内容，
        // 拖进来也解压不了 —— 与其让它到解压那一步再失败，不如这里就说清楚
        if (!File.Exists(item.Path))
            return "这个文件读不到，可能来自网盘的占位文件或压缩软件的临时目录。"
                   + "请先把文件存到本地文件夹，再从那里拖过来。";

        var extension = Path.GetExtension(item.Name);

        if (extension.IsNullOrEmpty() ||
            !AcceptedDropFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            return $"「{item.Name}」不是 JASM 认的 Mod 包。支持 .zip / .rar / .7z 压缩包、自解压 exe，"
                   + "或者把一个 Mod 文件夹整个拖进来。";

        return null;
    }

    /// <summary>
    /// 这一拖的文件夹判定用系统实际状态（<c>Directory.Exists</c>）而不是只看类型：
    /// 拖进来的项未必被包成 <see cref="StorageFolder"/>（压缩软件给的条目就不是），
    /// 而文件夹与文件在下面走的是完全不同的两条路。
    /// </summary>
    private static bool IsFolder(IStorageItem item) => item is StorageFolder || Directory.Exists(item.Path);

    public async Task DragDropModAsync(IReadOnlyList<IStorageItem> items)
    {
        if (DescribeDragDropRefusal(items) is { } refusal)
        {
            ShowDragDropRefused(refusal);
            return;
        }

        await CommandWrapperAsync(true, async () =>
        {
            try
            {
                var installMonitor = await Task.Run(async () =>
                    await _modDragAndDropService.AddStorageItemFoldersAsync(_modList, items).ConfigureAwait(false), CancellationToken);

                if (installMonitor is not null)
                    _ = installMonitor.Task.ContinueWith((task) => ModGridVM.QueueModRefresh(), CancellationToken);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Error while adding storage items.");
                _notificationService.ShowNotification(_localizer.GetLocalizedStringOrDefault("CharDetailsDndVM_DragDropFailedTitle", defaultValue: "Drag And Drop operation failed"),
                    string.Format(_localizer.GetLocalizedStringOrDefault("CharDetailsDndVM_DragDropAddStorageError", defaultValue: "An error occurred while adding the storage items. Reason:\n{0}"), e.Message),
                    TimeSpan.FromSeconds(5));
            }
        }).ConfigureAwait(false);
    }

    public bool CanDragDropModUrl(Uri? uri) => DescribeDragDropUrlRefusal(uri) is null;

    /// <summary>链接那条路的「为什么不收」，与 <see cref="DescribeDragDropRefusal"/> 同一个用途。</summary>
    public string? DescribeDragDropUrlRefusal(Uri? uri)
    {
        if (IsHardBusy)
            return "JASM 正忙（还有安装或移动任务在跑），等它做完再拖。";

        if (uri is null || !uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return "这里只接 http/https 的网页链接。要装本地 Mod，请把压缩包或文件夹拖进来。";

        if (!GameBananaUrlHelper.TryGetModIdFromUrl(uri, out _))
            return "这个链接不是 GameBanana 的 Mod 页面。目前只认 GameBanana 的 Mod 链接。";

        return null;
    }

    /// <summary>
    /// 把「为什么不收」当成一次失败告知用户。文案刻意不走本地化资源：它随 case 变化且要说得具体，
    /// 与浮窗 <c>OverlayViewModel.ErrorMessage</c> 那批状态文案同一路数（都是内联中文）。
    /// </summary>
    private void ShowDragDropRefused(string reason) =>
        _notificationService.ShowNotification(
            _localizer.GetLocalizedStringOrDefault("CharDetailsDndVM_DragDropFailedTitle", defaultValue: "Drag And Drop operation failed"),
            reason, TimeSpan.FromSeconds(8));

    public async Task DragDropModUrlAsync(Uri uri)
    {
        if (DescribeDragDropUrlRefusal(uri) is { } refusal)
        {
            ShowDragDropRefused(refusal);
            return;
        }

        await CommandWrapperAsync(true, async () =>
        {
            try
            {
                await _modDragAndDropService.AddModFromUrlAsync(_modList, uri);
            }
            catch (Exception e)
            {
                _logger.Error(e, "Error opening mod page window");
                _notificationService.ShowNotification(_localizer.GetLocalizedStringOrDefault("CharDetailsDndVM_ErrorOpeningModPageTitle", defaultValue: "Error opening mod page window"), e.Message, TimeSpan.FromSeconds(10));
            }
        }).ConfigureAwait(false);
    }
}