using System.Text;
using GIMI_ModManager.Core.Services.OilGloss;

namespace JASM.Tests;

/// <summary>
/// 油光扫描按官方说明实现：
/// 只包 <c>[TextureOverrideComponent3]</c> 及以后的段落（身体/衣物），
/// Component0–2（脸/头发/头饰）默认不包；Mod 没有 Component3+ 时退回包全部。
/// </summary>
public class OilGlossScannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jasm-oil-scan-" + Guid.NewGuid().ToString("N"));

    public OilGlossScannerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SelectsComponent3AndLaterDrawCalls()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideComponent0]
            drawindexed = 100, 0, 0

            [TextureOverrideComponent2]
            drawindexed = 300, 100, 0

            [TextureOverrideComponent3]
            drawindexed = 900, 400, 0

            [TextureOverrideComponent4]
            drawindexed = 400, 1300, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        // Component0–2 = 脸/头发，默认不包
        Assert.True(result.Drawcalls.Single(d => d.IndexCount == 100).IsHeadLike);
        Assert.Null(result.Drawcalls.Single(d => d.IndexCount == 100).Group);
        Assert.True(result.Drawcalls.Single(d => d.IndexCount == 300).IsHeadLike);
        Assert.Null(result.Drawcalls.Single(d => d.IndexCount == 300).Group);

        // Component3+ = 身体/衣物，全部包上
        Assert.Equal(OilGlossGroup.Body, result.Drawcalls.Single(d => d.IndexCount == 900).Group);
        Assert.Equal(OilGlossGroup.Body, result.Drawcalls.Single(d => d.IndexCount == 400).Group);
        Assert.Equal(900, result.Body!.IndexCount);
        Assert.Null(result.Cloth);
    }

    [Fact]
    public void SkipsModThatOnlyHasHeadComponents()
    {
        // 纯发型 / 只有脸和头的 Mod：Component0–2 全部排除，不能回退成「包全部」。
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideComponent0]
            drawindexed = 900, 0, 0

            [TextureOverrideComponent1]
            drawindexed = 500, 900, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.All(result.Drawcalls, d => Assert.True(d.IsHeadLike));
        Assert.All(result.Drawcalls, d => Assert.Null(d.Group));
        Assert.Null(result.Body);
        Assert.False(result.HasSelection);
        Assert.Contains("已全部排除", result.Reason);
    }

    [Fact]
    public void SelectsAllTextureOverridesWhenNoComponentSectionsExist()
    {
        // 作者用自定义段落名（没有 ComponentN）时，仍然包所有 TextureOverride。
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideBody]
            drawindexed = 900, 0, 0

            [TextureOverrideCloth]
            drawindexed = 500, 900, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.Equal(2, result.Drawcalls.Count(d => d.Group == OilGlossGroup.Body));
        Assert.Equal(900, result.Body!.IndexCount);
    }

    [Fact]
    public void ExcludesSectionsAndConditionsWithHeadKeywords()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideComponent3]
            drawindexed = 900, 0, 0

            [TextureOverrideComponent4]
            ; Draw hair long
            drawindexed = 500, 900, 0

            [TextureOverrideComponent5]
            if $hair_swap == 1
            drawindexed = 400, 1400, 0
            endif
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.Equal(OilGlossGroup.Body, result.Drawcalls.Single(d => d.IndexCount == 900).Group);
        Assert.True(result.Drawcalls.Single(d => d.IndexCount == 500).IsHeadLike);
        Assert.True(result.Drawcalls.Single(d => d.IndexCount == 400).IsHeadLike);
        Assert.Equal(1, result.Drawcalls.Count(d => d.Group is not null));
    }

    [Fact]
    public void IgnoresNonComponentSectionsWhenComponentSectionsExist()
    {
        // 官方身体/衣物在 Component3+；没有组件编号的自定义段落不参与，
        // 避免把作者额外的头发/道具段落包进去。
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideBodyExtra]
            drawindexed = 700, 0, 0

            [TextureOverrideComponent3]
            drawindexed = 900, 0, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.Single(result.Drawcalls, d => d.Group is not null);
        Assert.Equal(900, result.Body!.IndexCount);
    }

    [Fact]
    public void IgnoresDrawCallsInsideCommandLists()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [CommandListSomething]
            drawindexed = 900, 0, 0

            [TextureOverrideComponent3]
            drawindexed = 500, 900, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.Single(result.Drawcalls);
        Assert.Equal(500, result.Body!.IndexCount);
    }

    [Fact]
    public void IgnoresZeroCountDrawCalls()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideComponent3]
            drawindexed = 0, 0, 0
            drawindexed = 900, 0, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.Single(result.Drawcalls);
        Assert.Equal(900, result.Body!.IndexCount);
    }

    [Fact]
    public void SkipsDisabledFoldersAndJasmGeneratedFiles()
    {
        var mod = Path.Combine(_root, "Mod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mod);
        var keep = """
            namespace = TestMod
            [TextureOverrideComponent3]
            drawindexed = 900, 0, 0
            """;
        File.WriteAllText(Path.Combine(mod, "mod.ini"), keep, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(mod, "JASM_Oil.ini"), """
            [TextureOverrideComponent3]
            drawindexed = 111, 0, 0
            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(mod, "mod.ini.bak"), """
            [TextureOverrideComponent3]
            drawindexed = 222, 0, 0
            """, new UTF8Encoding(false));
        var disabled = Path.Combine(mod, "DISABLED_extra");
        Directory.CreateDirectory(disabled);
        File.WriteAllText(Path.Combine(disabled, "mod.ini"), """
            [TextureOverrideComponent3]
            drawindexed = 333, 0, 0
            """, new UTF8Encoding(false));

        var result = OilGlossScanner.Scan(mod);

        Assert.Single(result.Drawcalls);
        Assert.Equal(900, result.Body!.IndexCount);
    }

    [Fact]
    public void SkipsFaceAndHeadFiles()
    {
        var mod = Path.Combine(_root, "Mod-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(mod, "face"));
        File.WriteAllText(Path.Combine(mod, "face", "mod.ini"), """
            [TextureOverrideComponent3]
            drawindexed = 111, 0, 0
            """, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(mod, "mod.ini"), """
            namespace = TestMod
            [TextureOverrideComponent3]
            drawindexed = 900, 0, 0
            """, new UTF8Encoding(false));

        var result = OilGlossScanner.Scan(mod);

        Assert.Single(result.Drawcalls);
        Assert.Equal(900, result.Body!.IndexCount);
    }

    [Fact]
    public void ScansModsWhoseFolderNameMentionsHair()
    {
        // 「发型」只是 Mod 的名字；不少整模 Mod 其实带完整身体。
        var mod = Path.Combine(_root, "尤诺-C酱发型 by 布鲁加尔");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "mod.ini"), """
            namespace = TestMod

            [TextureOverrideComponent0]
            drawindexed = 100, 0, 0

            [TextureOverrideComponent4]
            drawindexed = 900, 100, 0
            """, new UTF8Encoding(false));

        var result = OilGlossScanner.Scan(mod);

        Assert.Equal(900, result.Body!.IndexCount);
        Assert.Null(result.Drawcalls.Single(d => d.IndexCount == 100).Group);
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
