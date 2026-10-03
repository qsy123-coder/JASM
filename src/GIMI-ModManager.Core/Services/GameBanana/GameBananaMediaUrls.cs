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