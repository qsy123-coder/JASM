using GIMI_ModManager.Core.Entities.Mods.Contract;
using GIMI_ModManager.Core.Entities.Mods.Helpers;
using GIMI_ModManager.Core.Entities.Mods.SkinMod;

namespace JASM.Tests;

/// <summary>
/// mod 封面路径的落盘 / 读回，以及「这张封面是哪张」的认定。
///
/// 锁的是非 ASCII 目录这一档：「整包装」（拖进来的自解压 exe）解出来之后，封面压在中文命名的
/// 子目录里（`长离-英招.exe\长离-英招\preview.png`）。落盘走的是相对路径，而
/// <c>Uri.MakeRelativeUri</c> 会把中文转义成 `%E9%95%BF...`，读回时那个 <c>%</c> 又被
/// <c>Path.Combine</c> + <c>Uri</c> 再转义一次（`%25E9...`）→ 指到不存在的目录 → 缩略图恒为占位图，
/// 而且启动扫描会把这份「指不到文件」的 ImagePath 抹成 null 落盘（记录就此丢掉）。
/// </summary>
public sealed class SkinModImagePathTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(),
        "jasm-mod-image-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // 临时目录清不掉不影响断言，交给系统。
        }
    }

    /// <summary>造一个「整包装」形状的 mod 目录：外层是 exe 名，封面压在中文命名的子目录里。</summary>
    private DirectoryInfo CreatePackedMod()
    {
        var modFolder = Directory.CreateDirectory(Path.Combine(_folder, "长离-英招.exe"));
        var inner = Directory.CreateDirectory(Path.Combine(modFolder.FullName, "长离-英招"));
        File.WriteAllBytes(Path.Combine(inner.FullName, "preview.png"), new byte[] { 1, 2, 3 });
        return modFolder;
    }

    [Fact]
    public async Task CoverInNonAsciiSubfolder_SurvivesSaveAndReload()
    {
        var modFolder = CreatePackedMod();
        var cover = new Uri(Path.Combine(modFolder.FullName, "长离-英招", "preview.png"));

        var mod = await SkinMod.CreateModAsync(modFolder);
        await mod.Settings.SaveSettingsAsync(new ModSettings(mod.Id, imagePath: cover));

        // 落盘的不能是百分号编码形式
        var json = await File.ReadAllTextAsync(Path.Combine(modFolder.FullName, ".JASM_ModConfig.json"));
        Assert.DoesNotContain("%E9%95%BF", json);

        // 重新读盘（= 下次启动扫描）必须还指得到那张图
        var reloaded = await SkinMod.CreateModAsync(modFolder);
        var settings = await reloaded.Settings.ReadSettingsAsync(useCache: true);

        Assert.NotNull(settings.ImagePath);
        Assert.True(File.Exists(settings.ImagePath!.LocalPath), settings.ImagePath.LocalPath);
    }

    /// <summary>旧版本落盘留下的百分号编码路径也要能读回来 —— 用户机上已经有一批这种配置。</summary>
    [Fact]
    public void PercentEncodedRelativePath_StillResolves()
    {
        var modFolder = CreatePackedMod();
        var encodedRelativePath = Uri.EscapeDataString("长离-英招") + "/preview.png";

        var resolved = SkinModHelpers.RelativeModPathToAbsPath(modFolder.FullName, encodedRelativePath);

        Assert.NotNull(resolved);
        Assert.True(File.Exists(resolved!.LocalPath), resolved.LocalPath);
    }

    /// <summary>认封面的顺序：根目录优先，根目录没有才往下探一层（不做全树递归）。</summary>
    [Fact]
    public void PreviewDetection_PrefersRootThenGoesOneLevelDown()
    {
        var modFolder = CreatePackedMod();
        var inSubFolder = Path.Combine(modFolder.FullName, "长离-英招", "preview.png");

        var detected = SkinModHelpers.DetectModPreviewImageIncludingSubfolders(modFolder.FullName);
        Assert.Equal(inSubFolder, detected?.LocalPath);

        // 根目录也有一张时，以根目录的为准
        var rootCover = Path.Combine(modFolder.FullName, "cover.png");
        File.WriteAllBytes(rootCover, new byte[] { 1 });

        detected = SkinModHelpers.DetectModPreviewImageIncludingSubfolders(modFolder.FullName);
        Assert.Equal(rootCover, detected?.LocalPath);
    }
}