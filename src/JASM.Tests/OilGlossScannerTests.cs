using System.Text;
using GIMI_ModManager.Core.Services.OilGloss;

namespace JASM.Tests;

/// <summary>
/// 油光自动分组：身体 = 最大 drawcall，衣物 = 排除头部后次大。
/// 同时锁定几处「不能包」的边界：头部、命令列表里的绘制、数量为 0、禁用目录。
/// </summary>
public class OilGlossScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jasm-oil-scan-" + Guid.NewGuid().ToString("N"));

    public OilGlossScannerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void PicksLargestAsBodyAndSecondLargestAsCloth()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideComponent0]
            hash = 1
            drawindexed = 100, 0, 0

            [TextureOverrideComponent1]
            drawindexed = 500, 100, 0

            [TextureOverrideComponent2]
            drawindexed = 900, 600, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.NotNull(result.Body);
        Assert.NotNull(result.Cloth);
        Assert.Equal(900, result.Body!.IndexCount);
        Assert.Equal(500, result.Cloth!.IndexCount);
        Assert.Equal(OilGlossGroup.Body, result.Body.Group);
        Assert.Equal(OilGlossGroup.Cloth, result.Cloth.Group);
    }

    [Fact]
    public void ExcludesHeadHairAndFaceDrawcalls()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideComponent0]
            ; Draw Component 0 hair
            drawindexed = 900, 0, 0

            [TextureOverrideComponent1]
            ; Draw Component 1 body
            drawindexed = 500, 900, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.NotNull(result.Body);
        Assert.Equal(500, result.Body!.IndexCount);
        Assert.Null(result.Cloth);
        Assert.True(result.Drawcalls.Single(d => d.IndexCount == 900).IsHeadLike);
    }

    [Fact]
    public void IgnoresDrawCallsInsideCommandLists()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [CommandListMod10SingleLayerDraw]
            drawindexed = 900, 0, 0

            [TextureOverrideComponent0]
            drawindexed = 100, 900, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.NotNull(result.Body);
        Assert.Equal(100, result.Body!.IndexCount);
        Assert.Single(result.Drawcalls);
    }

    [Fact]
    public void IgnoresZeroCountDrawCalls()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideComponent0]
            drawindexed = 0, 0, 0

            [TextureOverrideComponent1]
            drawindexed = 200, 0, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.Single(result.Drawcalls);
        Assert.Equal(200, result.Body!.IndexCount);
    }

    [Fact]
    public void SkipsDisabledFoldersAndJasmGeneratedFiles()
    {
        var mod = Path.Combine(_root, "Mod");
        Directory.CreateDirectory(Path.Combine(mod, "DISABLED_old"));
        Directory.CreateDirectory(Path.Combine(mod, "Normal"));

        File.WriteAllText(Path.Combine(mod, "DISABLED_old", "mod.ini"),
            "[TextureOverrideComponent0]\r\ndrawindexed = 900, 0, 0");

        // JASM 自己的产物不应被当成作者 drawcall
        File.WriteAllText(Path.Combine(mod, "Normal", "JASM_Oil.ini"),
            OilGlossProtocol.BuildOilIniText("JasmOilTest"));
        File.WriteAllText(Path.Combine(mod, "Normal", "mod.ini"), """
            namespace = TestMod
            [TextureOverrideComponent0]
            drawindexed = 100, 0, 0
            """, new UTF8Encoding(false));

        var result = OilGlossScanner.Scan(mod);

        Assert.Single(result.Drawcalls);
        Assert.Equal(100, result.Body!.IndexCount);
    }

    [Fact]
    public void ReportsReasonWhenOnlyHeadDrawCallsExist()
    {
        var mod = CreateMod("""
            namespace = TestMod
            [TextureOverrideComponent0]
            ; Draw Component 0 face
            drawindexed = 900, 0, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.Null(result.Body);
        Assert.True(result.Drawcalls.Single().IsHeadLike);
        Assert.Contains("头部", result.Reason);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string CreateMod(string iniContent)
    {
        var mod = Path.Combine(_root, "Mod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "mod.ini"), iniContent, new UTF8Encoding(false));
        return mod;
    }
}