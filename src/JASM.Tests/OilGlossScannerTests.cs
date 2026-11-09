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
    public void FallsBackToAllComponentsWhenNoComponent3Exists()
    {
        // 少数老 Mod 的身体就在 Component0：没有 3+ 时不能把整个 Mod 排除。
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideComponent0]
            drawindexed = 900, 0, 0

            [TextureOverrideComponent1]
            drawindexed = 500, 900, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.All(result.Drawcalls, d => Assert.False(d.IsHeadLike));
        Assert.All(result.Drawcalls, d => Assert.Equal(OilGlossGroup.Body, d.Group));
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
