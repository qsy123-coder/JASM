using System.Security.Cryptography;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using GIMI_ModManager.Core.Helpers;
using Microsoft.UI.Xaml;

namespace GIMI_ModManager.WinUI.Services;

public class ImageHandlerService
{
    private readonly string _tmpFolder = Path.Combine(App.TMP_DIR, "Images");
    private readonly string _cacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "JASM", "ImageCache");

    public readonly Uri PlaceholderImageUri = StaticPlaceholderImageUri;

    public static Uri StaticPlaceholderImageUri { get; } = new(Path.Combine(App.ASSET_DIR, "ModPanePlaceholder.webp"));

    private readonly IHttpClientFactory _httpClientFactory;

    public ImageHandlerService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;

        if (!Directory.Exists(_cacheFolder))
            Directory.CreateDirectory(_cacheFolder);
    }

    public string PlaceholderImagePath => PlaceholderImageUri.LocalPath;

    /// <summary>
    /// Get the current cache size in bytes.
    /// </summary>
    public long GetCacheSize()
    {
        if (!Directory.Exists(_cacheFolder)) return 0;
        return new DirectoryInfo(_cacheFolder)
            .GetFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length);
    }

    /// <summary>
    /// Clear all cached images.
    /// </summary>
    public void ClearCache()
    {
        if (!Directory.Exists(_cacheFolder)) return;
        foreach (var file in Directory.GetFiles(_cacheFolder))
            try { File.Delete(file); } catch { /* ignore */ }
    }

    public async Task<IStorageFile?> PickImageAsync(bool copyToTmpFolder = true, Window? window = null)
    {
        var filePicker = new FileOpenPicker();
        foreach (var supportedImageExtension in Constants.SupportedImageExtensions)
            filePicker.FileTypeFilter.Add(supportedImageExtension);

        filePicker.SuggestedStartLocation = PickerLocationId.PicturesLibrary;
        filePicker.SettingsIdentifier = "PickImage";

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window ?? App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(filePicker, hwnd);

        var file = await filePicker.PickSingleFileAsync();

        if (file == null) return null;

        if (copyToTmpFolder)
            file = await CopyImageToTmpFolder(file);

        return file;
    }


    public async Task<StorageFile> DownloadImageAsync(Uri url, CancellationToken cancellationToken = default)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Url must be https", nameof(url));

        if (!url.IsAbsoluteUri)
            throw new ArgumentException("Url must be absolute", nameof(url));

        var extension = Path.GetExtension(url.AbsolutePath);
        if (!Constants.SupportedImageExtensions.Contains(extension))
        {
            var invalidExtension = string.IsNullOrWhiteSpace(extension)
                ? "Could not determine extension"
                : extension;

            throw new ArgumentException($"Url must be a valid image url. Invalid extension: {invalidExtension}");
        }

        // Check cache first
        var cacheKey = GetCacheKey(url.ToString());
        var cacheFile = Path.Combine(_cacheFolder, cacheKey + extension);

        if (File.Exists(cacheFile))
        {
            try
            {
                return await StorageFile.GetFileFromPathAsync(cacheFile);
            }
            catch
            {
                // Cache file corrupt, re-download
                try { File.Delete(cacheFile); } catch { /* ignore */ }
            }
        }

        // Download to temp file first, then move to cache
        var tmpFolder = new DirectoryInfo(_tmpFolder);
        if (!tmpFolder.Exists)
            tmpFolder.Create();

        var tmpFile = Path.Combine(tmpFolder.FullName,
            $"WEB_DOWNLOAD_{Guid.NewGuid():N}{extension}");

        var client = _httpClientFactory.CreateClient();

        await using var responseStream = await client.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
        await using var tmpFileStream = File.Create(tmpFile);
        await responseStream.CopyToAsync(tmpFileStream, cancellationToken).ConfigureAwait(false);

        // Save to cache
        try
        {
            File.Copy(tmpFile, cacheFile, overwrite: true);
        }
        catch
        {
            // Cache write failed, still return the temp file
        }

        return await StorageFile.GetFileFromPathAsync(tmpFile);
    }

    private static string GetCacheKey(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }


    public static Task CopyImageToClipboardAsync(StorageFile file)
    {
        ArgumentNullException.ThrowIfNull(file, nameof(file));

        var package = new DataPackage
        {
            RequestedOperation = DataPackageOperation.Copy
        };


        var imageStream = RandomAccessStreamReference.CreateFromFile(file);
        package.SetBitmap(imageStream);
        package.SetStorageItems([file]);

        Clipboard.SetContent(package);
        Clipboard.Flush();
        return Task.CompletedTask;
    }

    public record ClipboardContainsImageResult(bool Result, DataPackageView? DataPackage = null);

    public async Task<ClipboardContainsImageResult> ClipboardContainsImageAsync()
    {
        var package = Clipboard.GetContent();
        if (package is null)
            return new ClipboardContainsImageResult(false, package);

        if (package.Contains(StandardDataFormats.Bitmap))
            return new ClipboardContainsImageResult(true, package);


        if (!package.Contains(StandardDataFormats.StorageItems))
            return new ClipboardContainsImageResult(false, package);

        var storageItems = await package.GetStorageItemsAsync();

        var containsValidFileExtension =
            storageItems.Any(item => Constants.SupportedImageExtensions.Contains(Path.GetExtension(item.Name), StringComparer.OrdinalIgnoreCase));

        return new ClipboardContainsImageResult(containsValidFileExtension, package);
    }

    public async Task<Uri?> GetImageFromClipboardAsync(DataPackageView? clipboardContent = null)
    {
        // Reuse the clipboard content if it's already been retrieved
        // Calling Clipboard.GetContent() then GetStorageItemsAsync() twice gives a COMException, or at least seems to be the case

        var package = clipboardContent ?? await ClipboardContainsImageAsync() switch
        {
            (true, { } dataPackage) => dataPackage,
            _ => null
        };


        if (package is null)
            return null;

        if (package.Contains(StandardDataFormats.StorageItems))
        {
            var storageItems = await package.GetStorageItemsAsync();

            var imageFile = storageItems.FirstOrDefault(item =>
                Constants.SupportedImageExtensions.Contains(Path.GetExtension(item.Name), StringComparer.OrdinalIgnoreCase));

            if (imageFile is null || !File.Exists(imageFile.Path))
                return null;

            var copiedImage = await CopyImageToTmpFolder(await StorageFile.GetFileFromPathAsync(imageFile.Path))
                .ConfigureAwait(false);

            return new Uri(copiedImage.Path);
        }

        if (!package.Contains(StandardDataFormats.Bitmap))
            return null;

        var imageStream = await package.GetBitmapAsync();
        var availableFormats = package.AvailableFormats;

        var fileExtension = Constants.SupportedImageExtensions.FirstOrDefault(supportedFormat =>
            availableFormats.Any(availableFormat =>
                availableFormat.TrimStart('.')
                    .Equals(supportedFormat.TrimStart('.'), StringComparison.OrdinalIgnoreCase)));

        if (fileExtension is null)
            return null;

        using var stream = await imageStream.OpenReadAsync();

        var tmpFile = await CopyStreamToTmpFolder(stream.AsStreamForRead(), fileExtension);

        return new Uri(tmpFile.Path);
    }

    private async Task<StorageFile> CopyStreamToTmpFolder(Stream stream, string extensionWithDot)
    {
        var tmpFolder = new DirectoryInfo(_tmpFolder);

        if (!tmpFolder.Exists) tmpFolder.Create();

        var tmpFile = Path.Combine(tmpFolder.FullName,
            $"STREAM_DOWNLOAD_{Guid.NewGuid():N}{extensionWithDot}");

        await using var fileStream = File.Create(tmpFile);
        await stream.CopyToAsync(fileStream);

        return await StorageFile.GetFileFromPathAsync(tmpFile);
    }

    private async Task<StorageFile> CopyImageToTmpFolder(StorageFile file)
    {
        var tmpFolder = new DirectoryInfo(_tmpFolder);

        if (!tmpFolder.Exists) tmpFolder.Create();

        var tmpFile = new FileInfo(Path.Combine(tmpFolder.FullName, file.Name));
        if (tmpFile.Exists) tmpFile.Delete();

        var tmpImage = await file.CopyAsync(await StorageFolder.GetFolderFromPathAsync(tmpFolder.FullName));
        var extension = tmpImage.FileType;

        var newFileName = $"{Path.GetFileNameWithoutExtension(tmpImage.Name)}_{Guid.NewGuid()}{extension}";

        await tmpImage.RenameAsync(newFileName);

        return tmpImage;
    }

    public async Task<StorageFile?> CopyImageToTmpFolder(Uri? uri)
    {
        if (uri is null)
            return null;

        if (uri.Scheme == Uri.UriSchemeHttps && uri.IsAbsoluteUri)
        {
            return await DownloadImageAsync(uri).ConfigureAwait(false);
        }

        if (uri.Scheme == Uri.UriSchemeFile)
        {
            var file = await StorageFile.GetFileFromPathAsync(uri.LocalPath);
            return await CopyImageToTmpFolder(file).ConfigureAwait(false);
        }

        return null;
    }
}