using GIMI_ModManager.Core.Services.GameBanana.ApiModels;

namespace GIMI_ModManager.Core.Services.GameBanana;

/// <summary>
/// 把 GameBanana 给的图片地址校验成可用地址：预览图（<c>_sBaseUrl</c> + / + <c>_sFile</c> 拼出来）、
/// 作者头像（<c>_aSubmitter._sAvatarUrl</c>）、分类图标（<c>_sIconUrl</c>）**走同一套规则**
/// —— 它们全部托管在 https 的 <c>images.gamebanana.com</c>（实测）。
///
/// 为什么要校验：这些字段来自用户提交的内容，直接丢给 <c>Image.Source</c> 等于把远端可控字符串
/// 当本地资源加载。把校验收在一处，新调用方就不会漏 —— 分类图标那条链路就是**漏了**的例子：
/// <c>ModStoreCategory</c> 曾按「图标应该和页面同域」去认 <c>gamebanana.com</c>，结果把真图标全丢了。
///
/// ⚠️ 现有 <c>ModPageInfo</c> 里已有一份**逐字等价**的实现（同一段拼接 + 同一套 host 判断）——
/// 商店这条链路刻意不动它：那个类已被 mod 详情页依赖，为一个新页面去改老代码，风险大于收益。
/// 等第三处调用者出现时再把两边合并到这里。
/// </summary>
public static class GameBananaMediaUrls
{
    /// <summary>只认这个图床 —— 预览图、头像、分类图标全部托管于此。</summary>
    private const string AllowedImageHost = "images.gamebanana.com";

    /// <summary>取一组可用的预览图地址；不合法的逐条丢弃（不是整组失败）。</summary>
    public static IReadOnlyList<Uri> GetPreviewImages(ApiImagesRoot? previewMedia)
    {
        if (previewMedia is null || previewMedia.Images.Length == 0)
            return [];

        List<Uri> images = [];
        foreach (var image in previewMedia.Images)
        {
            if (TryCreateImageUrl(image) is { } url)
                images.Add(url);
        }

        return images.AsReadOnly();
    }

    /// <summary>
    /// 取**第一张**预览图的缩略图地址（卡片只显示第一张）。
    ///
    /// 逐张试而不是只看第一张：第一张可能缺字段 / 非本图床，而 <see cref="GetPreviewImages"/>
    /// 会跳过后继续找下一张 —— 两边得给出「同一张图」，否则卡片会突然空着。
    /// </summary>
    public static Uri? GetPreviewThumbnail(ApiImagesRoot? previewMedia)
    {
        if (previewMedia is null)
            return null;

        foreach (var image in previewMedia.Images)
        {
            if (TryCreateThumbnailUrl(image) is { } url)
                return url;
        }

        return null;
    }

    /// <summary>
    /// 卡片封面用的**缩略图**地址：优先 GameBanana 自带的 530px 变体，缺变体时才退回原图。
    ///
    /// 与 <see cref="TryCreateImageUrl(ApiImageUrl)"/> 分成两个方法是有意的：详情抽屉的画廊要原图，
    /// 只有列表卡片该用缩略图。实测同一张图原图 804 KB / 3.1 s、530 变体 55 KB / 0.37 s ——
    /// 一页 15 张卡就是 12 MB 和 0.8 MB 的差别。
    /// </summary>
    public static Uri? TryCreateThumbnailUrl(ApiImageUrl image)
    {
        if (string.IsNullOrWhiteSpace(image.BaseUrl))
            return null;

        // 变体文件与原图同目录（实测），拼法与校验都跟原图那条一样。
        if (!string.IsNullOrWhiteSpace(image.File530) &&
            TryCreateImageUrl($"{image.BaseUrl}/{image.File530}") is { } thumbnail)
        {
            return thumbnail;
        }

        return TryCreateImageUrl(image);
    }

    /// <summary>
    /// 拼不出合法地址（缺字段 / 非 https / 非本图床）就返回 null，**绝不抛异常**。
    /// </summary>
    public static Uri? TryCreateImageUrl(ApiImageUrl image)
    {
        // 缺 _sFile 时会拼出 ".../" 这种尾巴：Uri.TryCreate 能过，但请求必然 404，提前判掉。
        if (string.IsNullOrWhiteSpace(image.BaseUrl) || string.IsNullOrWhiteSpace(image.ImageId))
            return null;

        return TryCreateImageUrl(image.BaseUrl + "/" + image.ImageId);
    }

    /// <summary>
    /// 校验一条**接口直接给全的**地址（<c>_sAvatarUrl</c> / <c>_sIconUrl</c>）。
    ///
    /// 空串要当「没有」处理：实测部分记录的分类图标是 <c>""</c>（不是缺键），
    /// <c>Uri.TryCreate("")</c> 本来就过不了，这里只是把这条实测事实写下来。
    /// </summary>
    public static Uri? TryCreateImageUrl(string? rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl) ||
            !Uri.TryCreate(rawUrl, UriKind.Absolute, out var url))
            return null;

        if (url.Scheme != Uri.UriSchemeHttps ||
            !url.Host.Equals(AllowedImageHost, StringComparison.OrdinalIgnoreCase))
            return null;

        return url;
    }
}