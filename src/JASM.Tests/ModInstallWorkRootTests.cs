using GIMI_ModManager.Core.Services;

namespace JASM.Tests;

/// <summary>
/// 拖拽安装的解压临时目录落在**哪块盘**上。
///
/// 静默安装的提速全靠这一件事：临时目录摊在目标那块盘上，「装」就是同卷改名（瞬间）；
/// 摊在 %TEMP%（多半是 C:）而 Mod 目录在别的盘上，装的时候就得把整个包再复制一遍 ——
/// 实测那一趟复制跟解压本身一样贵。
/// </summary>
public sealed class ModInstallWorkRootTests
{
    private static readonly string VolumeRoot = Path.GetPathRoot(Path.GetTempPath())!;

    [Fact]
    public void FollowsTheVolumeOfTheTargetFolder()
    {
        var target = Path.Combine(VolumeRoot, "XXMI", "WWMI", "Mods", "character", "changli");

        var workRoot = DragAndDropScanner.ResolveWorkRoot(target);

        Assert.True(string.Equals(Path.GetPathRoot(workRoot), Path.GetPathRoot(target),
            StringComparison.OrdinalIgnoreCase), workRoot);
        Assert.EndsWith("JASM_TMP", workRoot, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FallsBackToTempWithoutAHint()
    {
        Assert.Equal(Path.Combine(Path.GetTempPath(), "JASM_TMP"), DragAndDropScanner.ResolveWorkRoot(null));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "JASM_TMP"), DragAndDropScanner.ResolveWorkRoot("  "));
    }
}