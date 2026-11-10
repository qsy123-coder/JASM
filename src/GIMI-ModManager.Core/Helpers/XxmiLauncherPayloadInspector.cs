using System.Text;

namespace GIMI_ModManager.Core.Helpers;

/// <summary>
/// 「装好的 XXMI 启动器能不能起来」的磁盘检查。
///
/// 启动器本体是 Python(GUI) 程序：<c>Resources\Bin</c> 里既有 Python 运行时，也有 PyAV 的 C 扩展
/// （<c>av\**\*.pyd</c>）和它依赖的 FFmpeg DLL（<c>av.libs\*.dll</c>）。这些 .pyd 静态链到**带哈希后缀的
/// 具体文件名**（例如 <c>avutil-61-dde6…dll</c>），所以「.pyd 与 av.libs 来自不同版本」或「av.libs 少了
/// 一个 DLL」都会让 <c>import av</c> 在初始化阶段抛 DLL 加载失败，启动器随后弹出
/// 「启动器在初始化期间崩溃」的致命错误窗（用户看到一屏 traceback）。
///
/// 因此这里查的是**导入表**而不是「目录在不在」：文件齐、版本错（安装被中断、被旧版残留盖掉一半）
/// 时目录照样在，只有导入表能指出真正缺的是哪一份 DLL。
///
/// 为什么放 Core：WinUI 的一键配置（判定「需修复」）与启动器进程的启动前置检查要用同一份判断，
/// 而且放这里能被 JASM.Tests 覆盖（WinUI 工程不在测试项目的引用范围内）。
/// </summary>
public static class XxmiLauncherPayloadInspector
{
    /// <summary>启动器 exe 文件名（<c>Resources\Bin</c> 下，不是 XXMI 根目录）。</summary>
    public const string LauncherExeFileName = "XXMI Launcher.exe";

    private const string ResourcesFolderName = "Resources";
    private const string BinFolderName = "Bin";
    private const string PyAvFolderName = "av";
    private const string PyAvLibsFolderName = "av.libs";

    /// <summary><c>Resources\Bin</c>：启动器负载所在的相对目录（清理旧版残留时就扫这一层）。</summary>
    public static string BinRelativePath => Path.Combine(ResourcesFolderName, BinFolderName);

    /// <summary>XXMI 根目录下启动器 exe 的绝对路径。</summary>
    public static string GetLauncherExePath(string xxmiRootFolder)
        => Path.Combine(xxmiRootFolder, BinRelativePath, LauncherExeFileName);

    /// <summary>
    /// 判断一个命令路径是不是「JASM 认的 XXMI 启动器」—— 只看布局
    /// （<c>…\Resources\Bin\XXMI Launcher.exe</c>）。用户手工配置的其它 exe 一律不适用本检查。
    /// </summary>
    public static bool IsXxmiLauncherExe(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return false;

        if (!string.Equals(Path.GetFileName(exePath.TrimEnd('\\', '/')), LauncherExeFileName,
                StringComparison.OrdinalIgnoreCase))
            return false;

        var binFolder = Path.GetDirectoryName(exePath.TrimEnd('\\', '/'));
        if (binFolder is null || !string.Equals(Path.GetFileName(binFolder), BinFolderName,
                StringComparison.OrdinalIgnoreCase))
            return false;

        var resourcesFolder = Path.GetDirectoryName(binFolder);
        return resourcesFolder is not null
               && string.Equals(Path.GetFileName(resourcesFolder), ResourcesFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 返回启动器负载的问题清单；空清单 = 看起来能起来。文案直接面向用户（中文），
    /// 调用方把它们拼进「需修复」说明或启动前的拦截提示。
    /// </summary>
    /// <remarks>
    /// 只查「能被证明会失败」的条件，不做完整校验：exe 不在、PyAV 的 <c>av</c> / <c>av.libs</c> 只剩一半、
    /// 以及 .pyd 的导入表里点了名但 av.libs 里没有的 FFmpeg DLL。两套目录**都不在**时不报问题 ——
    /// 早期版本可能压根不带 PyAV，不去猜它该有什么。
    /// </remarks>
    public static IReadOnlyList<string> FindProblems(string? launcherExePath)
    {
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(launcherExePath) || !File.Exists(launcherExePath))
        {
            problems.Add($"未找到启动器程序 {launcherExePath}");
            return problems;
        }

        var binFolder = Path.GetDirectoryName(launcherExePath)!;
        var avFolder = Path.Combine(binFolder, PyAvFolderName);
        var libsFolder = Path.Combine(binFolder, PyAvLibsFolderName);

        var hasAv = Directory.Exists(avFolder);
        var hasLibs = Directory.Exists(libsFolder);

        if (!hasAv && !hasLibs)
            return problems;

        if (!hasAv || !hasLibs)
        {
            problems.Add(hasAv
                ? $"缺少 {PyAvLibsFolderName}\\（{binFolder}）"
                : $"缺少 {PyAvFolderName}\\（{binFolder}）");
            return problems;
        }

        var presentDlls = EnumerateFilesSafe(libsFolder, "*.dll")
            .Select(Path.GetFileName)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (presentDlls.Count == 0)
        {
            problems.Add($"{PyAvLibsFolderName}\\ 里没有任何 DLL（{libsFolder}）");
            return problems;
        }

        // .pyd → 它点名要的 FFmpeg DLL。缺一个，import av 就会在那一行抛
        // 「DLL load failed while importing …」，启动器直接进入致命错误分支。
        var missing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extensionModule in EnumerateFilesSafe(avFolder, "*.pyd"))
            foreach (var importedDll in PeImportTable.ReadImportedDllNames(extensionModule))
            {
                if (IsBundledFfmpegLibrary(importedDll) && !presentDlls.Contains(importedDll))
                    missing.Add(importedDll);
            }

        if (missing.Count > 0)
        {
            var listed = string.Join("、", missing.Take(3));
            var suffix = missing.Count > 3 ? $" 等 {missing.Count} 个" : string.Empty;
            problems.Add($"{PyAvLibsFolderName}\\ 缺少 {listed}{suffix}");
        }

        return problems;
    }

    /// <summary>「看起来健康」的简写，供判定「需修复」用。</summary>
    public static bool IsHealthy(string? launcherExePath) => FindProblems(launcherExePath).Count == 0;

    /// <summary>
    /// PyAV 随包分发的 FFmpeg DLL 名：<c>avutil-61-&lt;hash&gt;.dll</c>（delvewheel 改写过的）或
    /// <c>avutil-61.dll</c>（老构建）。系统里没有同名 DLL，所以不会把 <c>avrt.dll</c> 这类误判进来。
    /// </summary>
    private static bool IsBundledFfmpegLibrary(string name)
    {
        if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            return false;

        bool hasKnownPrefix = name.StartsWith("avcodec-", StringComparison.OrdinalIgnoreCase)
                              || name.StartsWith("avdevice-", StringComparison.OrdinalIgnoreCase)
                              || name.StartsWith("avfilter-", StringComparison.OrdinalIgnoreCase)
                              || name.StartsWith("avformat-", StringComparison.OrdinalIgnoreCase)
                              || name.StartsWith("avutil-", StringComparison.OrdinalIgnoreCase)
                              || name.StartsWith("swscale-", StringComparison.OrdinalIgnoreCase)
                              || name.StartsWith("swresample-", StringComparison.OrdinalIgnoreCase);

        if (!hasKnownPrefix)
            return false;

        // 前缀之后必须是版本号（`-61` / `-61-<hash>`），否则是别的名字（例如某个第三方 avfoo-extra.dll）。
        var rest = name[(name.IndexOf('-') + 1)..];
        return rest.Length > 0 && char.IsAsciiDigit(rest[0]);
    }

    /// <summary>目录可能不存在或不可读（权限 / 并发删除），枚举失败按「没有文件」处理。</summary>
    private static IEnumerable<string> EnumerateFilesSafe(string folder, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(folder, pattern, SearchOption.AllDirectories).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// 只读 PE 的导入表，取出它依赖的 DLL 名。不是通用 PE 解析器：够用即可，任何结构不对的地方
    /// 都返回空清单（宁可漏报，也不要在启动路径上抛异常）。
    /// </summary>
    private static class PeImportTable
    {
        /// <summary>超过这个大小的「.pyd」显然不是我们关心的东西，别去读它。</summary>
        private const int MaxFileBytes = 32 * 1024 * 1024;

        /// <summary>导入描述符表的迭代上限，防止畸形文件把我们卡住。</summary>
        private const int MaxDescriptors = 4096;

        public static IReadOnlyList<string> ReadImportedDllNames(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    bufferSize: 4096);
                if (stream.Length < 0x40 || stream.Length > MaxFileBytes)
                    return Array.Empty<string>();

                using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

                if (ReadUInt16At(stream, reader, 0) != 0x5A4D) // "MZ"
                    return Array.Empty<string>();

                var peOffset = ReadInt32At(stream, reader, 0x3C); // e_lfanew
                if (peOffset <= 0 || peOffset + 24L > stream.Length)
                    return Array.Empty<string>();

                stream.Position = peOffset;
                if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
                    return Array.Empty<string>();

                reader.ReadUInt16();                        // Machine
                var sectionCount = reader.ReadUInt16();
                reader.ReadUInt32();                        // TimeDateStamp
                reader.ReadUInt32();                        // PointerToSymbolTable
                reader.ReadUInt32();                        // NumberOfSymbols
                var optionalHeaderSize = reader.ReadUInt16();
                reader.ReadUInt16();                        // Characteristics

                var optionalHeaderOffset = stream.Position;
                var dataDirectoryOffset = reader.ReadUInt16() switch
                {
                    0x10B => optionalHeaderOffset + 96,  // PE32
                    0x20B => optionalHeaderOffset + 112, // PE32+
                    _ => -1
                };

                if (dataDirectoryOffset < 0)
                    return Array.Empty<string>();

                // DataDirectory[1] = Import Table（[0] 是 Export，跳过 8 字节）
                var importRva = ReadUInt32At(stream, reader, dataDirectoryOffset + 8);
                if (importRva == 0)
                    return Array.Empty<string>();

                var sectionTableOffset = optionalHeaderOffset + optionalHeaderSize;
                if (sectionTableOffset < optionalHeaderOffset
                    || sectionTableOffset + (long)sectionCount * 40 > stream.Length)
                    return Array.Empty<string>();

                var sections = new Section[sectionCount];
                for (var i = 0; i < sectionCount; i++)
                {
                    stream.Position = sectionTableOffset + i * 40L;
                    reader.ReadBytes(8);                    // Name
                    var virtualSize = reader.ReadUInt32();
                    var virtualAddress = reader.ReadUInt32();
                    var rawSize = reader.ReadUInt32();
                    var rawPointer = reader.ReadUInt32();
                    sections[i] = new Section(virtualAddress, Math.Max(virtualSize, rawSize), rawPointer);
                }

                var importDescriptorOffset = RvaToFileOffset(sections, importRva);
                if (importDescriptorOffset is null)
                    return Array.Empty<string>();

                var names = new List<string>();
                for (var index = 0; index < MaxDescriptors; index++)
                {
                    var descriptorOffset = importDescriptorOffset.Value + index * 20L;
                    if (descriptorOffset + 20 > stream.Length)
                        break;

                    stream.Position = descriptorOffset;
                    reader.ReadUInt32();                        // OriginalFirstThunk
                    reader.ReadUInt32();                        // TimeDateStamp
                    reader.ReadUInt32();                        // ForwarderChain
                    var nameRva = reader.ReadUInt32();
                    var firstThunk = reader.ReadUInt32();

                    // 全零描述符是表尾；只剩 Name 为 0 的条目当作空洞跳过。
                    if (nameRva == 0 && firstThunk == 0)
                        break;
                    if (nameRva == 0)
                        continue;

                    var nameOffset = RvaToFileOffset(sections, nameRva);
                    if (nameOffset is null)
                        continue;

                    var name = ReadAsciiZ(stream, reader, nameOffset.Value);
                    if (!string.IsNullOrEmpty(name))
                        names.Add(name);
                }

                return names;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                          or NotSupportedException)
            {
                return Array.Empty<string>();
            }
        }

        private static ushort ReadUInt16At(Stream stream, BinaryReader reader, long offset)
        {
            if (offset < 0 || offset + 2 > stream.Length) return 0;
            stream.Position = offset;
            return reader.ReadUInt16();
        }

        private static int ReadInt32At(Stream stream, BinaryReader reader, long offset)
        {
            if (offset < 0 || offset + 4 > stream.Length) return -1;
            stream.Position = offset;
            return reader.ReadInt32();
        }

        private static uint ReadUInt32At(Stream stream, BinaryReader reader, long offset)
        {
            if (offset < 0 || offset + 4 > stream.Length) return 0;
            stream.Position = offset;
            return reader.ReadUInt32();
        }

        private static long? RvaToFileOffset(Section[] sections, uint rva)
        {
            foreach (var section in sections)
            {
                if (rva < section.VirtualAddress) continue;
                var delta = rva - section.VirtualAddress;
                if (delta >= section.VirtualSize) continue;
                return section.RawPointer + (long)delta;
            }

            return null;
        }

        private static string? ReadAsciiZ(Stream stream, BinaryReader reader, long offset)
        {
            if (offset < 0 || offset >= stream.Length) return null;

            stream.Position = offset;
            var bytes = new List<byte>(32);
            for (var i = 0; i < 260 && stream.Position < stream.Length; i++)
            {
                var value = reader.ReadByte();
                if (value == 0) break;
                bytes.Add(value);
            }

            return bytes.Count == 0 ? null : Encoding.ASCII.GetString(bytes.ToArray());
        }

        private readonly record struct Section(uint VirtualAddress, uint VirtualSize, uint RawPointer);
    }
}