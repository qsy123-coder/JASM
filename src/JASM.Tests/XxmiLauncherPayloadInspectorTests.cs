using System.Text;
using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="XxmiLauncherPayloadInspector"/> — the disk check that decides whether an installed
/// XXMI Launcher can start at all.
///
/// 现场：某些用户点「启动 3Dmigoto」时启动器弹「启动器在初始化期间崩溃」，traceback 停在
/// <c>av/codec/hwaccel</c> —— 即 PyAV 的扩展模块找不到它链接的 FFmpeg DLL。这里覆盖的判定正是
/// 「扩展模块导入表里点名的 DLL 在 av.libs 里存不存在」，它必须能从导入表读出来，而不是只看目录在不在
/// （版本混装时目录、文件名都在，只有导入表能说出缺的是哪一份）。
///
/// 测试自己造一个最小 PE（PE32+，一个 .idata 节 + 导入描述符），不依赖本机任何 XXMI 安装。
/// </summary>
public sealed class XxmiLauncherPayloadInspectorTests : IDisposable
{
    private const string AvUtilDll = "avutil-61-dde604963d441a4b3674ebd03e0c8e8c.dll";
    private const string AvCodecDll = "avcodec-63-bd8d3f94b351ee58146d18ee9ad04fe6.dll";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "jasm-launcher-payload-tests",
        Guid.NewGuid().ToString("N"));

    public XxmiLauncherPayloadInspectorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响任何结论。
        }
    }

    private string BinFolder => Path.Combine(_root, "Resources", "Bin");
    private string LauncherExePath => Path.Combine(BinFolder, "XXMI Launcher.exe");
    private string AvFolder => Path.Combine(BinFolder, "av");
    private string AvLibsFolder => Path.Combine(BinFolder, "av.libs");

    [Fact]
    public void BuildsTheLauncherPathFromTheXxmiRoot()
    {
        Assert.Equal(Path.Combine(@"D:\XXMI", "Resources", "Bin", "XXMI Launcher.exe"),
            XxmiLauncherPayloadInspector.GetLauncherExePath(@"D:\XXMI"));
    }

    [Theory]
    [InlineData(@"D:\XXMI\Resources\Bin\XXMI Launcher.exe", true)]
    [InlineData(@"D:\XXMI\Resources\Bin\XxmiLauncher.exe", false)]
    [InlineData(@"D:\XXMI\bin\XXMI Launcher.exe", false)]
    [InlineData(@"D:\XXMI\Resources\XXMI Launcher.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RecognisesOnlyTheResourcesBinLayout(string? path, bool expected)
    {
        Assert.Equal(expected, XxmiLauncherPayloadInspector.IsXxmiLauncherExe(path));
    }

    [Fact]
    public void ReportsAMissingLauncherExecutable()
    {
        var problems = XxmiLauncherPayloadInspector.FindProblems(LauncherExePath);

        var problem = Assert.Single(problems);
        Assert.Contains("未找到启动器程序", problem);
    }

    [Fact]
    public void HealthyLayoutHasNoProblems()
    {
        CreateLauncher(pydImports: new[] { AvUtilDll }, libs: new[] { AvUtilDll, AvCodecDll });

        Assert.Empty(XxmiLauncherPayloadInspector.FindProblems(LauncherExePath));
        Assert.True(XxmiLauncherPayloadInspector.IsHealthy(LauncherExePath));
    }

    [Fact]
    public void ReportsAMissingAvLibsFolder()
    {
        CreateLauncher(pydImports: new[] { AvUtilDll }, libs: Array.Empty<string>(), createLibsFolder: false);

        var problem = Assert.Single(XxmiLauncherPayloadInspector.FindProblems(LauncherExePath));
        Assert.Contains("av.libs", problem);
    }

    [Fact]
    public void ReportsAnEmptyAvLibsFolder()
    {
        CreateLauncher(pydImports: new[] { AvUtilDll }, libs: Array.Empty<string>());

        var problem = Assert.Single(XxmiLauncherPayloadInspector.FindProblems(LauncherExePath));
        Assert.Contains("没有任何 DLL", problem);
    }

    [Fact]
    public void ReportsTheFfmpegDllTheImportTableAsksForButTheLibsFolderLacks()
    {
        // 版本混装：扩展模块要 avutil-61-<hashA>，av.libs 里只有另一代的 avutil-59 —— 文件名、目录都在，
        // 只有导入表能指出这一份缺失（import av 会在这里直接抛 DLL load failed）。
        CreateLauncher(pydImports: new[] { AvUtilDll }, libs: new[]
        {
            AvCodecDll,
            "avutil-59-0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f0f.dll"
        });

        var problem = Assert.Single(XxmiLauncherPayloadInspector.FindProblems(LauncherExePath));
        Assert.Contains(AvUtilDll, problem);
    }

    [Fact]
    public void IgnoresImportsThatAreNotBundledFfmpegLibraries()
    {
        // 系统 DLL 与 Python 运行时不在 av.libs 里，缺了也不该报 —— 那不是这个检查能判断的事。
        CreateLauncher(pydImports: new[] { "KERNEL32.dll", "python3.dll", "VCRUNTIME140.dll" },
            libs: new[] { AvCodecDll });

        Assert.Empty(XxmiLauncherPayloadInspector.FindProblems(LauncherExePath));
    }

    [Fact]
    public void UnreadableExtensionModulesAreNotAnError()
    {
        // 不是合法 PE（例如被截断/被杀的半成品）时按「看不出来」处理，不能在启动路径上抛异常。
        CreateLauncher(pydImports: Array.Empty<string>(), libs: new[] { AvCodecDll },
            garbagePyd: new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        Assert.Empty(XxmiLauncherPayloadInspector.FindProblems(LauncherExePath));
    }

    /// <summary>
    /// 造一份启动器目录：<c>Resources\Bin\XXMI Launcher.exe</c> + <c>av\codec\hwaccel.pyd</c>
    /// （内容是最小 PE，导入表里只列 <paramref name="pydImports"/>）+ <c>av.libs\*.dll</c>。
    /// </summary>
    private void CreateLauncher(string[] pydImports, string[] libs, bool createLibsFolder = true,
        byte[]? garbagePyd = null)
    {
        Directory.CreateDirectory(BinFolder);
        File.WriteAllBytes(LauncherExePath, new byte[] { 0x4D, 0x5A });

        var codecFolder = Path.Combine(AvFolder, "codec");
        Directory.CreateDirectory(codecFolder);
        var extensionModule = Path.Combine(codecFolder, "hwaccel.pyd");
        if (garbagePyd is not null)
            File.WriteAllBytes(extensionModule, garbagePyd);
        else
            WriteMinimalPortableExecutable(extensionModule, pydImports);

        if (!createLibsFolder)
            return;

        Directory.CreateDirectory(AvLibsFolder);
        foreach (var dll in libs)
            File.WriteAllBytes(Path.Combine(AvLibsFolder, dll), new byte[] { 0x4D, 0x5A });
    }

    /// <summary>
    /// 写一个能被导入表解析器读通的最小 PE32+：DOS 头 → PE 头 → 一个 <c>.idata</c> 节
    /// （导入描述符表 + DLL 名，最后一条全零描述符表示表尾）。
    /// </summary>
    private static void WriteMinimalPortableExecutable(string path, string[] importedDlls)
    {
        const int peOffset = 0x40;
        const int optionalHeaderSize = 0xF0;
        const uint sectionRva = 0x1000;
        const int sectionFileOffset = 0x200;
        const int sectionSize = 0x200;
        const int descriptorSize = 20;
        const int dataDirectoryOffset = 112; // PE32+ 的可选头里 DataDirectory 的起点

        var bytes = new byte[sectionFileOffset + sectionSize];

        // DOS 头 + e_lfanew
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(peOffset).CopyTo(bytes, 0x3C);

        // PE 签名
        BitConverter.GetBytes(0x00004550u).CopyTo(bytes, peOffset);

        // COFF 头
        var coff = peOffset + 4;
        BitConverter.GetBytes((ushort)0x8664).CopyTo(bytes, coff);              // Machine
        BitConverter.GetBytes((ushort)1).CopyTo(bytes, coff + 2);               // NumberOfSections
        BitConverter.GetBytes((ushort)optionalHeaderSize).CopyTo(bytes, coff + 16);
        BitConverter.GetBytes((ushort)0x2022).CopyTo(bytes, coff + 18);         // Characteristics

        // 可选头（PE32+）：只填 DataDirectory[1] = 导入表
        var optionalHeader = coff + 20;
        BitConverter.GetBytes((ushort)0x20B).CopyTo(bytes, optionalHeader);
        BitConverter.GetBytes(16u).CopyTo(bytes, optionalHeader + 108);         // NumberOfRvaAndSizes
        BitConverter.GetBytes(sectionRva).CopyTo(bytes, optionalHeader + dataDirectoryOffset + 8);
        BitConverter.GetBytes((uint)((importedDlls.Length + 1) * descriptorSize))
            .CopyTo(bytes, optionalHeader + dataDirectoryOffset + 12);

        // 节表（一个 .idata）
        var sectionTable = optionalHeader + optionalHeaderSize;
        Encoding.ASCII.GetBytes(".idata").CopyTo(bytes, sectionTable);
        BitConverter.GetBytes((uint)sectionSize).CopyTo(bytes, sectionTable + 8);   // VirtualSize
        BitConverter.GetBytes(sectionRva).CopyTo(bytes, sectionTable + 12);         // VirtualAddress
        BitConverter.GetBytes((uint)sectionSize).CopyTo(bytes, sectionTable + 16);  // SizeOfRawData
        BitConverter.GetBytes((uint)sectionFileOffset).CopyTo(bytes, sectionTable + 20); // PointerToRawData

        // 节内容：导入描述符（Name 是 RVA）+ 名字串
        var nameOffsetInSection = (importedDlls.Length + 1) * descriptorSize;
        for (var i = 0; i < importedDlls.Length; i++)
        {
            var descriptor = sectionFileOffset + i * descriptorSize;
            BitConverter.GetBytes(1u).CopyTo(bytes, descriptor);                     // OriginalFirstThunk
            BitConverter.GetBytes(sectionRva + (uint)nameOffsetInSection)
                .CopyTo(bytes, descriptor + 12);                                     // Name (RVA)
            BitConverter.GetBytes(1u).CopyTo(bytes, descriptor + 16);                // FirstThunk ≠ 0 → 不是表尾

            var name = Encoding.ASCII.GetBytes(importedDlls[i] + "\0");
            name.CopyTo(bytes, sectionFileOffset + nameOffsetInSection);
            nameOffsetInSection += name.Length;
        }

        File.WriteAllBytes(path, bytes);
    }
}