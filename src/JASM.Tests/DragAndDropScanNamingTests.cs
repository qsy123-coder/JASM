using GIMI_ModManager.Core.Services;

namespace JASM.Tests;

/// <summary>
/// 解压临时目录该叫什么（<see cref="DragAndDropScanner.GetExtractionFolderName"/>）。
///
/// <para>
/// 这一层会被当成包的目录名一路带进 Mods 目录：容器文件的扩展名留在上面，装出来的 Mod 就叫
/// <c>xxx.exe</c>（实机反馈：拖进来的自解压包全都带个 exe 后缀）。扩展名是容器的、不是 Mod 名字的一部分。
/// </para>
///
/// <para>
/// 但这条只对<b>文件</b>成立：拖进来的文件夹名里的点不是扩展名，<c>Mod v1.2</c> 不能被截成 <c>Mod v1</c>。
/// </para>
/// </summary>
public sealed class DragAndDropScanNamingTests
{
    [Theory]
    [InlineData(@"C:\Mods\心(含大招)-点绛唇v2.4（内附切换）by PebbleRox.exe", "心(含大招)-点绛唇v2.4（内附切换）by PebbleRox")]
    [InlineData(@"C:\Mods\Xuanling.zip", "Xuanling")]
    [InlineData(@"C:\Mods\Mod v1.2.7z", "Mod v1.2")]
    [InlineData(@"C:\Mods\NoExtension", "NoExtension")]
    public void ContainerExtensionsAreDropped(string path, string expected)
    {
        Assert.Equal(expected, DragAndDropScanner.GetExtractionFolderName(path));
    }

    [Fact]
    public void AFolderKeepsItsNameEvenWhenItContainsDots()
    {
        var tempRoot = Directory.CreateTempSubdirectory("jasm-dot-name-");
        try
        {
            var folder = new DirectoryInfo(Path.Combine(tempRoot.FullName, "Mod v1.2"));
            folder.Create();

            Assert.Equal("Mod v1.2", DragAndDropScanner.GetExtractionFolderName(folder.FullName));
        }
        finally
        {
            try
            {
                tempRoot.Delete(true);
            }
            catch (Exception)
            {
                // 临时目录删不掉不该让测试失败
            }
        }
    }
}