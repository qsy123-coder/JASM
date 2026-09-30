using GIMI_ModManager.Core.Helpers;

namespace JASM.Tests;

/// <summary>
/// Covers <see cref="ModPackageRootResolver"/> —— 也就是「拖进来的包是<b>整个</b>一个 Mod，
/// 还是里面塞了一堆互不相干的东西」这个判断。
///
/// <para>
/// 判错的后果是实测过的：达妮娅的「可调体型切换版」包里，根目录带着作者手写的
/// <c>Master_daniya.ini</c>（按键切换全在里面），底下 5 个变体子目录各带自己的 <c>mod.ini</c>。
/// 向导的启发式取「树里第一个 <c>mod.ini</c>」，于是只装了 <c>世界熊</c> 一个子目录 ——
/// 用户看到的现象是「解压出来明明一堆东西，装完只有一个文件夹，热键也没反应」。
/// </para>
///
/// <para>
/// 测试在临时目录里搭目录结构，不依赖任何真实样本。
/// </para>
/// </summary>
public class ModPackageRootResolverTests : IDisposable
{
    /// <summary>模拟 <c>%TEMP%\JASM_TMP\&lt;guid&gt;</c> 这一层。</summary>
    private readonly DirectoryInfo _extractedRoot;

    public ModPackageRootResolverTests()
    {
        _extractedRoot = Directory.CreateTempSubdirectory("jasm-mod-root-");
    }

    public void Dispose()
    {
        try
        {
            _extractedRoot.Delete(true);
        }
        catch (Exception)
        {
            // 临时目录删不掉不该让测试失败
        }
    }

    // ---------- 包装层剥离 ----------

    /// <summary>拖拽那条路解出来的真实形状：guid 根 → 「原文件名」目录 → SFX 同名目录 → 包内容。</summary>
    [Fact]
    public void TheWrapperLayersAroundThePackageArePeeledOff()
    {
        MakeDirectory(@"达妮娅-原版切换 by weiwuxc888.exe\达妮娅-原版切换 by weiwuxc888\世界熊\Meshes");
        MakeDirectory(@"达妮娅-原版切换 by weiwuxc888.exe\达妮娅-原版切换 by weiwuxc888\界面\Textures");
        MakeFile(@"达妮娅-原版切换 by weiwuxc888.exe\达妮娅-原版切换 by weiwuxc888\Master_daniya.ini");

        var contentRoot = ModPackageRootResolver.ResolveContentRoot(_extractedRoot);

        Assert.Equal("达妮娅-原版切换 by weiwuxc888", contentRoot.Name);
    }

    /// <summary>单 Mod 包：剥掉 <c>&lt;包名&gt;.7z</c> 这层后停在 Mod 目录（它有 ini 和 Meshes，不是单子目录）。</summary>
    [Fact]
    public void ASingleModFolderIsItsOwnContentRoot()
    {
        MakeFile(@"xuanling.7z\Xuanling_Pyroath\mod.ini");
        MakeDirectory(@"xuanling.7z\Xuanling_Pyroath\Meshes");

        var contentRoot = ModPackageRootResolver.ResolveContentRoot(_extractedRoot);

        Assert.Equal("Xuanling_Pyroath", contentRoot.Name);
    }

    /// <summary>载荷是散文件（没有顶层目录）时，别顺着「只有一个子目录」一路挖进 <c>Meshes</c> 里。</summary>
    [Fact]
    public void APayloadOfLooseFilesStopsTheDescent()
    {
        MakeFile(@"xuanling.rar\mod.ini");
        MakeDirectory(@"xuanling.rar\Meshes");

        var contentRoot = ModPackageRootResolver.ResolveContentRoot(_extractedRoot);

        Assert.Equal("xuanling.rar", contentRoot.Name);
    }

    /// <summary>什么都没解出来时原样返回，不抛异常（后面还有失败处理去给用户说法）。</summary>
    [Fact]
    public void AnEmptyExtractionRootIsReturnedAsIs()
    {
        var contentRoot = ModPackageRootResolver.ResolveContentRoot(_extractedRoot);

        Assert.Equal(_extractedRoot.FullName, contentRoot.FullName);
    }

    // ---------- 「根就是 Mod」判定 ----------

    /// <summary>本次事故的形状：根带手写 ini + 若干变体子目录 ⇒ 整个包是一个 Mod。</summary>
    [Fact]
    public void ARootWithAnIniAndSubFoldersIsItselfTheMod()
    {
        var root = MakeDirectory("达妮娅可调体型切换版");
        MakeFile(@"达妮娅可调体型切换版\Master_daniya.ini");
        MakeFile(@"达妮娅可调体型切换版\世界熊\mod.ini");
        MakeDirectory(@"达妮娅可调体型切换版\界面\Textures");

        Assert.True(ModPackageRootResolver.LooksLikeSelfContainedModRoot(root));
    }

    /// <summary>根 ini 的名字是作者自己起的（<c>Master_xxx.ini</c>），不能只认 <c>mod.ini</c>。</summary>
    [Fact]
    public void TheRootIniDoesNotHaveToBeCalledModIni()
    {
        var root = MakeDirectory("pack");
        MakeFile(@"pack\MyHandWritten_Keys.ini");
        MakeDirectory(@"pack\variant");

        Assert.True(ModPackageRootResolver.LooksLikeSelfContainedModRoot(root));
    }

    /// <summary>只有 ini、没有子目录 = 散落的 ini，不当 Mod 根。</summary>
    [Fact]
    public void ALooseIniWithoutSubFoldersIsNotAModRoot()
    {
        MakeFile("d3dx.ini");

        Assert.False(ModPackageRootResolver.LooksLikeSelfContainedModRoot(_extractedRoot));
    }

    /// <summary>一堆互不相干的 Mod 打成一个包（ini 全在子目录里）⇒ 不拍板，交给向导原有的启发式。</summary>
    [Fact]
    public void SeveralModFoldersWithNoIniAtTheRootAreNotAModRoot()
    {
        MakeFile(@"ModA\mod.ini");
        MakeFile(@"ModB\mod.ini");

        Assert.False(ModPackageRootResolver.LooksLikeSelfContainedModRoot(_extractedRoot));
    }

    /// <summary>说明文件 + 一个目录，也不是 Mod。</summary>
    [Fact]
    public void AReadmeNextToAFolderIsNotAModRoot()
    {
        MakeFile("readme.txt");
        MakeDirectory("Docs");

        Assert.False(ModPackageRootResolver.LooksLikeSelfContainedModRoot(_extractedRoot));
    }

    [Fact]
    public void AnEmptyFolderIsNotAModRoot()
    {
        Assert.False(ModPackageRootResolver.LooksLikeSelfContainedModRoot(_extractedRoot));
    }

    // ---------- 两者串起来 ----------

    /// <summary>端到端判定：达妮娅的包剥掉包装层后，应当被认成「整包一个 Mod」。</summary>
    [Fact]
    public void TheDeniaStylePackageResolvesToTheWholePack()
    {
        const string inner = @"达妮娅-原版切换 by weiwuxc888.exe\达妮娅-原版切换 by weiwuxc888\达妮娅可调体型切换版";

        MakeFile(inner + @"\Master_daniya.ini");
        MakeFile(inner + @"\Read me-DeniaBodyTypeToggleMod.txt");
        MakeFile(inner + @"\世界熊\mod.ini");
        MakeDirectory(inner + @"\世界脸装饰\Meshes");
        MakeDirectory(inner + @"\世界身体\Textures");
        MakeDirectory(inner + @"\大招脸装饰\Meshes");
        MakeDirectory(inner + @"\界面\Textures");

        var contentRoot = ModPackageRootResolver.ResolveContentRoot(_extractedRoot);

        Assert.Equal("达妮娅可调体型切换版", contentRoot.Name);
        Assert.True(ModPackageRootResolver.LooksLikeSelfContainedModRoot(contentRoot));
    }

    // ---------- 夹具 ----------

    private DirectoryInfo MakeDirectory(string relativePath)
    {
        var directory = new DirectoryInfo(Path.Combine(_extractedRoot.FullName, relativePath));
        directory.Create();
        return directory;
    }

    private void MakeFile(string relativePath)
    {
        var file = new FileInfo(Path.Combine(_extractedRoot.FullName, relativePath));
        file.Directory!.Create();
        File.WriteAllText(file.FullName, string.Empty);
    }
}