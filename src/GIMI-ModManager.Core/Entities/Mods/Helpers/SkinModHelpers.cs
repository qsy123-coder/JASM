using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Helpers;

namespace GIMI_ModManager.Core.Entities.Mods.Helpers;

public static class SkinModHelpers
{
    public static string? UriPathToModRelativePath(ISkinMod mod, string? uriPath)
    {
        if (string.IsNullOrWhiteSpace(uriPath))
            return null;

        var modPath = mod.FullPath;

        var modUri = Uri.TryCreate(modPath, UriKind.Absolute, out var result) &&
                     result.Scheme == Uri.UriSchemeFile
            ? result
            : null;

        var uri = Uri.TryCreate(uriPath, UriKind.Absolute, out var uriResult) &&
                  uriResult.Scheme == Uri.UriSchemeFile
            ? uriResult
            : null;

        // This is technically the only path that should be used.
        if (modUri is not null && uri is not null)
        {
            var relativeUri = modUri.MakeRelativeUri(uri);

            var modName = modUri.Segments.LastOrDefault();
            if (string.IsNullOrWhiteSpace(modName))
                modName = mod.Name;

            var relativePath = relativeUri.OriginalString.Replace($"{modName}/", "");

            // 落盘要的是**普通**路径（`长离-英招/preview.png`）：MakeRelativeUri 的 OriginalString
            // 会把非 ASCII 转义成 `%E9%95%BF...`，而读取端 RelativeModPathToAbsPath 是拿
            // Path.Combine 拼的、会把那个 % 再转义一次（`%25E9...`）→ 指到不存在的目录。
            // 两端都按普通路径来，编码只当传输形式、不落盘。
            return Uri.UnescapeDataString(relativePath);
        }


        if (Uri.IsWellFormedUriString(uriPath, UriKind.Absolute))
        {
            var filename = Path.GetFileName(uriPath);
            return string.IsNullOrWhiteSpace(filename) ? null : filename;
        }

        var absPath = Path.GetFileName(uriPath);

        var file = Path.GetFileName(absPath);
        return string.IsNullOrWhiteSpace(file) ? null : file;
    }

    public static Uri? RelativeModPathToAbsPath(string modPath, string? relativeModPath)
    {
        if (string.IsNullOrWhiteSpace(relativeModPath))
            return null;

        // 落盘的相对路径可能是**百分号编码**的：旧版本写出 `%E9%95%BF%E7%A6%BB-%E8%8B%B1%E6%8B%9B/preview.png`
        // （见 UriPathToModRelativePath），用户机上已经有一批这种配置。直接 Path.Combine 交给 Uri，
        // 那个 % 会被再转义一次（`%25E9...`）→ LocalPath 落在一个不存在的目录上 → 封面永远加载不出来
        // （表现为画廊与浮窗的缩略图恒为占位图）。先解码成普通路径再拼。
        var decodedRelativePath = Uri.UnescapeDataString(relativeModPath);

        var uri = Uri.TryCreate(Path.Combine(modPath, decodedRelativePath), UriKind.Absolute, out var result) &&
                  result.Scheme == Uri.UriSchemeFile
            ? result
            : null;

        return uri;
    }

    public static bool IsInModFolder(ISkinMod mod, Uri path)
    {
        if (path.Scheme != Uri.UriSchemeFile)
            return false;

        var fsPath = path.LocalPath;


        return fsPath.StartsWith(mod.FullPath, StringComparison.OrdinalIgnoreCase);
    }


    public static Uri? StringUrlToUri(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        return Uri.IsWellFormedUriString(url, UriKind.Absolute) ? new Uri(url) : null;
    }

    public static Guid StringToGuid(string? guid)
    {
        if (string.IsNullOrWhiteSpace(guid))
            return Guid.NewGuid();

        return Guid.TryParse(guid, out var result) ? result : Guid.NewGuid();
    }

    public static readonly string[] _imageNamePriority = new[] { ".jasm_cover", "preview", "cover" };

    public static Uri[] DetectModPreviewImages(string modDirPath)
    {
        var modDir = new DirectoryInfo(modDirPath);
        if (!modDir.Exists)
            return Array.Empty<Uri>();

        var images = new List<FileInfo>();
        foreach (var file in modDir.EnumerateFiles())
        {
            if (!_imageNamePriority.Any(i => file.Name.ToLower().StartsWith(i)))
                continue;


            var extension = file.Extension.ToLower();
            if (!Constants.SupportedImageExtensions.Contains(extension))
                continue;

            images.Add(file);
        }

        // Sort images by priority
        foreach (var imageName in _imageNamePriority.Reverse())
        {
            var image = images.FirstOrDefault(x => x.Name.ToLower().StartsWith(imageName));
            if (image is null)
                continue;

            images.Remove(image);
            images.Insert(0, image);
        }

        return images.Select(x => new Uri(x.FullName)).ToArray();
    }

    /// <summary>
    /// 认一张封面：先看 mod 根目录，找不到再往下探一层。
    ///
    /// 「整包装」（拖进来的自解压 exe）解出来之后，mod 根是**外层**（exe 名那一层），
    /// preview.png 常跟真正的内容一起压在子目录里 —— 只看根目录会一张都认不到。
    /// 只多探一层、不做全树递归：再深就可能把某个变体子目录里的图认成整包的封面。
    /// </summary>
    public static Uri? DetectModPreviewImageIncludingSubfolders(string modDirPath)
    {
        var inRoot = DetectModPreviewImages(modDirPath);
        if (inRoot.Length > 0)
            return inRoot[0];

        var modDir = new DirectoryInfo(modDirPath);
        if (!modDir.Exists)
            return null;

        foreach (var subFolder in modDir.EnumerateDirectories())
        {
            var inSubFolder = DetectModPreviewImages(subFolder.FullName);
            if (inSubFolder.Length > 0)
                return inSubFolder[0];
        }

        return null;
    }
}