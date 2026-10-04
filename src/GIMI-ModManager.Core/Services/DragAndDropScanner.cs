using System.Diagnostics;
using System.Text;
using GIMI_ModManager.Core.Contracts.Entities;
using GIMI_ModManager.Core.Entities;
using GIMI_ModManager.Core.Helpers;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.Zip;
using SharpCompress.Common;

namespace GIMI_ModManager.Core.Services;

// Extract process if archive file:
// 1. Copy archive to work folder in windows temp folder
// 2. Extract archive to windows temp folder
// 3. Move extracted files to work folder
// 4. Delete copied archive
//
// 自解压包（.exe = PE 存根 + 追加归档，如 WinRAR SFX）走同一条路，但**不执行它**：
// 先按文件头确认这个 exe 真的追加了归档（SfxPayloadDetector），再让内置 7-Zip 把它当压缩包读。
// 只问「7-Zip 能不能打开」是不行的 —— 实测它对普通 PE 也「成功」，抽出来的是 .text/.rdata 这些节。
// 两个例外：
//   - 自解压包**不复制**一份到临时目录：这类包动辄几十~几百 MB，而它就在用户自己的磁盘上
//     （复制是为了防 7-Zip 拖拽那种「源随时被删」的情形，见 ModDragAndDropService 的注释）；
//   - 自解压包**只能**走内置 7-Zip：SharpCompress 读不了 SFX。

public sealed class DragAndDropScanner
{
    private const string WorkFolderName = "JASM_TMP";

    private readonly ILogger _logger = Log.ForContext<DragAndDropScanner>();
    private readonly string _tmpFolder;

    /// <summary>本次扫描独占的临时目录（<c>&lt;工作根&gt;\&lt;guid&gt;</c>）。失败/取消时由调用方删掉。</summary>
    private readonly string _workFolderRoot;

    // Extracts files to this folder（初值就是 _workFolderRoot，ScanAndGetContents 会再往下拼一层源文件名）
    private string _workFolder;

    private ExtractTool _extractTool;

    /// <param name="targetFolderHint">
    /// 这一单最终要落到哪儿（角色的 Mod 目录、或根目录都行）—— 只用它**判断在哪块盘上解压**，
    /// 见 <see cref="ResolveWorkRoot"/>。给不出来就退回 <c>%TEMP%</c>。
    /// </param>
    public DragAndDropScanner(string? targetFolderHint = null)
    {
        _extractTool = GetExtractTool();
        _tmpFolder = ResolveWorkRoot(targetFolderHint);
        _workFolderRoot = Path.Combine(_tmpFolder, Guid.NewGuid().ToString("N"));
        _workFolder = _workFolderRoot;
    }

    /// <summary>
    /// 解压摊到哪块盘上：优先**目标所在的那块卷**（<c>D:\JASM_TMP</c>），给不出提示或卷根写不了
    /// 就退回 <c>%TEMP%\JASM_TMP</c>。
    ///
    /// <para>
    /// 为什么值得绕这一下：装的时候要把整个包搬进角色的 Mod 目录，而**同卷搬 = 改名**（瞬间）、
    /// 跨卷搬 = 把整个包再复制一遍。实测一个包「解压 10s + 复制 9s」—— 那一趟复制跟解压一样贵，
    /// 它唯一的作用就是把数据从 C: 挪到 D:。摊在目标盘上，这一步就退化成改名，等于省掉一半。
    /// </para>
    ///
    /// <para>
    /// 卷根不可写（权限 / 只读盘）时退回 %TEMP%：那条路只是慢，不会坏。
    /// </para>
    /// </summary>
    public static string ResolveWorkRoot(string? targetFolderHint)
    {
        if (!string.IsNullOrWhiteSpace(targetFolderHint))
        {
            try
            {
                var volumeRoot = Path.GetPathRoot(Path.GetFullPath(targetFolderHint));
                if (!string.IsNullOrWhiteSpace(volumeRoot))
                {
                    var candidate = Path.Combine(volumeRoot, WorkFolderName);
                    Directory.CreateDirectory(candidate);
                    return candidate;
                }
            }
            catch (Exception)
            {
                // 卷根不可写（权限/只读）—— 退回 %TEMP%，那条路只是慢
            }
        }

        return Path.Combine(Path.GetTempPath(), WorkFolderName);
    }

    /// <summary>
    /// 把拖进来的东西摊开到临时目录，返回「安装时该当成根的那个文件夹」。
    /// </summary>
    /// <param name="path">压缩包 / 自解压 exe / 文件夹的路径。</param>
    /// <param name="password">压缩包密码；不需要密码的包传 <c>null</c>。<b>不会写进日志或异常消息。</b></param>
    /// <exception cref="ArchiveExtractionException">
    /// 认不出这是个包、需要密码、密码不对、包坏了、解压完一个文件都没有 —— 按
    /// <see cref="ArchiveExtractionException.Reason"/> 区分，各自给用户不同的话。
    /// </exception>
    public DragAndDropScanResult ScanAndGetContents(string path, string? password = null)
    {
        PrepareWorkFolder();

        _workFolder = Path.Combine(_workFolderRoot, Path.GetFileName(path));

        // 扩展名不在白名单里也可能是个自解压包（.exe），但**必须**先按文件头确认它真的追加了归档：
        // 实测 7-Zip 对普通 PE 也会「成功」（把 PE 的节当文件抽出来），只看「7z 能打开」等于把任何 exe 都收下
        var isSelfExtractingArchive = !IsArchive(path) && SfxPayloadDetector.TryDetect(path) is not null;

        if (IsArchive(path) || isSelfExtractingArchive)
        {
            string extractSource;
            if (isSelfExtractingArchive)
            {
                extractSource = path; // 不复制，见文件头注释
            }
            else
            {
                var copiedArchive = new FileInfo(path);
                extractSource = copiedArchive.CopyTo(Path.Combine(_tmpFolder, Path.GetFileName(path)), true)
                    .FullName;
            }

            Extract(extractSource, password, isSelfExtractingArchive);
            ThrowIfNothingWasExtracted();
        }
        else if (Directory.Exists(path)) // ModDragAndDropService handles loose folders, but this added just in case
        {
            var modFolder = new Mod(new DirectoryInfo(path));
            modFolder.CopyTo(_workFolder);
            ThrowIfNothingWasExtracted();
        }
        else
        {
            throw new ArchiveExtractionException(ArchiveExtractionFailureReason.NotAnArchive,
                "No valid mod folder or archive found");
        }


        return new DragAndDropScanResult()
        {
            // 也就是 _workFolderRoot（_workFolder 是它的下一层，装的是摊开后的内容）
            ExtractedFolder = new Mod(new DirectoryInfo(_workFolder).Parent!),
            TempFolder = _workFolderRoot
        };
    }

    /// <summary>
    /// 删掉本次扫描的临时目录。
    ///
    /// <para>
    /// <b>只在失败 / 用户取消时调用</b>：安装成功那条路上，安装向导还在异步读这个目录里的文件。
    /// 删除失败只记日志不抛 —— 收尾清理失败不该把取消流程本身搞砸。
    /// </para>
    /// </summary>
    public void CleanupWorkFolder()
    {
        try
        {
            if (Directory.Exists(_workFolderRoot))
                Directory.Delete(_workFolderRoot, true);
        }
        catch (Exception e)
        {
            _logger.Warning(e, "Failed to clean up the temporary work folder {Folder}", _workFolderRoot);
        }
    }

    private void PrepareWorkFolder()
    {
        Directory.CreateDirectory(_tmpFolder);
        Directory.CreateDirectory(_workFolderRoot);
    }

    private bool IsArchive(string path)
    {
        return Path.GetExtension(path) switch
        {
            ".zip" => true,
            ".rar" => true,
            ".7z" => true,
            _ => false
        };
    }

    /// <summary>
    /// 解压。
    ///
    /// <para>
    /// 注意这里**没有任何一条路会静默什么都不做**：原先是「挑到一个解压器就调用，挑不到就算了」，
    /// 结果是 SharpCompress 模式下解压 .exe（或任何没登记扩展名）会摊出一个空目录，然后被当成
    /// 一个空 Mod 装进去 —— 用户只会看到「装了但游戏里没反应」。
    /// </para>
    /// </summary>
    private void Extract(string path, string? password, bool isSelfExtractingArchive)
    {
        // 自解压包只能走内置 7-Zip
        if (_extractTool == ExtractTool.Bundled7Zip || isSelfExtractingArchive)
        {
            Extract7Z(path, password);
            return;
        }

        if (_extractTool == ExtractTool.SharpCompress)
        {
            switch (Path.GetExtension(path))
            {
                case ".zip":
                    SharpExtractZip(path);
                    return;
                case ".rar":
                    SharpExtractRar(path);
                    return;
                case ".7z":
                    SharpExtract7z(path);
                    return;
                default:
                    throw new ArchiveExtractionException(ArchiveExtractionFailureReason.ToolFailed,
                        $"No extractor available for '{Path.GetExtension(path)}' archives");
            }
        }

        if (_extractTool == ExtractTool.System7Zip) throw new NotImplementedException();
    }

    private void ExtractEntries(IArchive archive)
    {
        _logger.Information("Extracting {ArchiveType} archive", archive.Type);
        foreach (var entry in archive.Entries)
        {
            _logger.Debug("Extracting {EntryName}", entry.Key);
            entry.WriteToDirectory(_workFolder, new ExtractionOptions()
            {
                ExtractFullPath = true,
                Overwrite = true,
                PreserveFileTime = false
            });
        }
    }

    private void SharpExtractZip(string path)
    {
        using var archive = ZipArchive.Open(path);
        ExtractEntries(archive);
    }


    private void SharpExtractRar(string path)
    {
        using var archive = RarArchive.Open(path);
        ExtractEntries(archive);
    }

    // ReSharper disable once InconsistentNaming
    private void SharpExtract7z(string path)
    {
        using var archive = ArchiveFactory.Open(path);
        ExtractEntries(archive);
    }


    private enum ExtractTool
    {
        Bundled7Zip, // 7zip bundled with JASM
        SharpCompress, // SharpCompress library
        System7Zip // 7zip installed on the system
    }

    private ExtractTool GetExtractTool()
    {
        var bundled7ZFolder = Path.Combine(AppContext.BaseDirectory, @"Assets\7z\");
        if (File.Exists(Path.Combine(bundled7ZFolder, "7z.exe")) &&
            File.Exists(Path.Combine(bundled7ZFolder, "7-zip.dll")) &&
            File.Exists(Path.Combine(bundled7ZFolder, "7z.dll")))
        {
            _logger.Debug("Using bundled 7zip");
            return ExtractTool.Bundled7Zip;
        }

        _logger.Information("Bundled 7zip not found, using SharpCompress library");
        return ExtractTool.SharpCompress;
    }


    private void Extract7Z(string path, string? password)
    {
        var sevenZipPath = Path.Combine(AppContext.BaseDirectory, @"Assets\7z\7z.exe");
        if (!File.Exists(sevenZipPath))
        {
            _logger.Error("Bundled 7z.exe not found at {SevenZipPath}", sevenZipPath);
            throw new ArchiveExtractionException(ArchiveExtractionFailureReason.ToolFailed,
                "Bundled 7z.exe not found");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = sevenZipPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 三个流都要接管：stdin 关掉是为了让 7-Zip **不再等密码输入**（否则它会一直等着，
            // 把这次拖拽连同后台线程一起挂死）；stdout/stderr 要读出来才能分辨失败原因
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // 7-Zip 默认按控制台代码页输出，中文路径在日志里会是乱码
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // 用 ArgumentList 而不是拼 Arguments 字符串：密码里可能有空格 / 引号 / 中文，
        // 交给 .NET 逐个转义才不会拼错（拼错的表现是「密码明明对却报密码错」）
        startInfo.ArgumentList.Add("x");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add($"-o{_workFolder}");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-sccUTF-8");
        if (!string.IsNullOrEmpty(password))
            startInfo.ArgumentList.Add($"-p{password}");

        _logger.Information("Extracting archive with command: {Command}", RedactCommand(startInfo));

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        process.StandardInput.Close();

        // 两个流都得边跑边读，否则任一管道写满就会把 7-Zip 卡住，WaitForExit 永不返回
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();

        var failure = ArchiveExtractionClassifier.Classify(process.ExitCode, stdout, stderr,
            !string.IsNullOrEmpty(password));

        if (failure is not null)
        {
            // 异常消息里只带退出码：密码绝不能出现在异常里（它会一路飘到日志和用户看得到的通知上）
            _logger.Warning("7z extraction failed with exit code {ExitCode}: {StdErr}", process.ExitCode,
                Excerpt(stderr));
            throw new ArchiveExtractionException(failure.Value,
                $"7-Zip extraction failed with exit code {process.ExitCode}");
        }

        if (process.ExitCode == ArchiveExtractionClassifier.WarningExitCode)
            _logger.Warning("7z extraction finished with warnings: {StdErr}", Excerpt(stderr));

        _logger.Information("7z extraction finished with exit code {ExitCode}", process.ExitCode);
    }

    /// <summary>
    /// 解压「成功」但一个条目都没出来 = 半失败。实测不支持的加密包就可能只留下一个空文件夹，
    /// 宁可在这里报错，也不要让用户走到安装向导才发现装了个空的。
    /// </summary>
    private void ThrowIfNothingWasExtracted()
    {
        if (Directory.Exists(_workFolder) && Directory.EnumerateFileSystemEntries(_workFolder).Any())
            return;

        _logger.Warning("Nothing was extracted to {WorkFolder}", _workFolder);
        throw new ArchiveExtractionException(ArchiveExtractionFailureReason.EmptyOutput,
            "Nothing was extracted from the archive");
    }

    /// <summary>
    /// 拼一条**可以进日志**的命令行：<c>-p{密码}</c> 一律打码。
    /// 取命令行务必走这里 —— 密码不得出现在日志里是这个功能的硬约束。
    /// </summary>
    private static string RedactCommand(ProcessStartInfo startInfo)
    {
        var arguments = startInfo.ArgumentList
            .Select(argument => argument.StartsWith("-p", StringComparison.Ordinal) ? "-p***" : argument);

        return $"{startInfo.FileName} {string.Join(' ', arguments)}";
    }

    /// <summary>把可能很长的进程输出截断，只用于日志。</summary>
    private static string Excerpt(string? value, int maxLength = 2000)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : string.Concat(trimmed.AsSpan(0, maxLength), "…");
    }
}

public class DragAndDropScanResult
{
    public IMod ExtractedFolder { get; init; } = null!;

    /// <summary>摊开内容所在的临时目录（<c>%TEMP%\JASM_TMP\&lt;guid&gt;</c>，即 <see cref="ExtractedFolder"/> 本身）。</summary>
    public string TempFolder { get; init; } = string.Empty;

    public string[] IgnoredMods { get; init; } = Array.Empty<string>();
}
