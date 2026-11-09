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

    [Fact]
    public void PrefersDrawCallsVisibleInDefaultToggleState()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [Constants]
            global persist $swapvar_toggle_0 = 0
            global $draw_component_3_005 = 1
            global $draw_component_3_002 = 1

            [CommandListProcessToggles]
            $draw_component_3_005 = ($swapvar_toggle_0 == 1)
            $draw_component_3_002 = ($swapvar_toggle_0 == 0)

            [TextureOverrideComponent3]
            if $draw_component_3_005
            drawindexed = 900, 0, 0
            endif
            if $draw_component_3_002
            drawindexed = 500, 900, 0
            endif
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.Equal(500, result.Body!.IndexCount);
        Assert.Null(result.Cloth);
        Assert.False(result.Drawcalls.Single(d => d.IndexCount == 900).VisibleByDefault);
        Assert.True(result.Drawcalls.Single(d => d.IndexCount == 500).VisibleByDefault);
    }

    [Fact]
    public void EvaluatesCompoundToggleExpressions()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [Constants]
            global persist $swapvar_toggle_1 = 0

            [CommandListProcessToggles]
            $draw_component_3 = ($swapvar_toggle_1 == -1) || ($swapvar_toggle_1 == 0) || ($swapvar_toggle_1 == 1)

            [TextureOverrideComponent3]
            if $draw_component_3
            drawindexed = 700, 0, 0
            endif
            if $draw_component_3_035
            drawindexed = 900, 700, 0
            endif
            """);

        var result = OilGlossScanner.Scan(mod);

        // $draw_component_3 用默认值求值为真；未定义的 _3_035 按可见处理
        Assert.Equal(900, result.Body!.IndexCount); // 未知可见性按可见处理，仍按大小选
        Assert.True(result.Drawcalls.Single(d => d.IndexCount == 700).VisibleByDefault);
    }

    [Fact]
    public void FallsBackToAllCandidatesWhenEverythingIsHidden()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [Constants]
            global persist $swapvar_toggle_0 = 0

            [CommandListProcessToggles]
            $draw_component_3_005 = ($swapvar_toggle_0 == 1)

            [TextureOverrideComponent3]
            if $draw_component_3_005
            drawindexed = 900, 0, 0
            endif
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.Equal(900, result.Body!.IndexCount);
    }

    [Fact]
    public void GroupsAllMeshesSharingBodyMaterialAndEverythingElseAsCloth()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideComponent3]
            ResourceTexture23
            if $draw_component_3_002
            drawindexed = 900, 0, 0
            endif
            ResourceTexture23
            if $draw_component_3_006
            drawindexed = 500, 100, 0
            endif

            [TextureOverrideComponent4]
            ResourceTexture11
            drawindexed = 400, 200, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.Equal(OilGlossGroup.Body, result.Drawcalls.Single(d => d.IndexCount == 900).Group);
        Assert.Equal(OilGlossGroup.Body, result.Drawcalls.Single(d => d.IndexCount == 500).Group);
        Assert.Equal(OilGlossGroup.Cloth, result.Drawcalls.Single(d => d.IndexCount == 400).Group);
        Assert.Equal(900, result.Body!.IndexCount);
        Assert.Equal(400, result.Cloth!.IndexCount);
    }

    [Fact]
    public void ExcludesHeadZoneByIndexPositionButKeepsLargest()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideComponent0]
            drawindexed = 1000, 0, 0

            [TextureOverrideComponent1]
            drawindexed = 5000, 2000, 0

            [TextureOverrideComponent2]
            drawindexed = 3000, 10000, 0

            [TextureOverrideComponent3]
            drawindexed = 2000, 20000, 0

            [TextureOverrideComponent4]
            drawindexed = 1000, 30000, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.True(result.Drawcalls.Single(d => d.StartIndex == 0).IsHeadLike);
        // 最大块落在 5%–10%：保留为身体
        Assert.False(result.Drawcalls.Single(d => d.StartIndex == 2000).IsHeadLike);
        Assert.False(result.Drawcalls.Single(d => d.StartIndex == 10000).IsHeadLike);
    }

    [Fact]
    public void ExcludesExtremeHeadLargestAndKeepsNextBody()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideComponent0]
            drawindexed = 30000, 0, 0

            [TextureOverrideComponent1]
            drawindexed = 20000, 4000, 0

            [TextureOverrideComponent2]
            drawindexed = 10000, 20000, 0

            [TextureOverrideComponent3]
            drawindexed = 5000, 30000, 0

            [TextureOverrideComponent4]
            drawindexed = 2000, 40000, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        // 0%–5% 的最大块是头发，即使索引数最大也排除
        Assert.True(result.Drawcalls.Single(d => d.StartIndex == 0).IsHeadLike);
        Assert.Equal(20000, result.Body!.IndexCount);
    }

    [Fact]
    public void RejectsHairOnlyModWhoseDrawsAllSitInHeadZone()
    {
        var mod = CreateMod("""
            namespace = TestMod

            [TextureOverrideComponent0]
            drawindexed = 1000, 0, 0

            [TextureOverrideComponent1]
            drawindexed = 1000, 1000, 0

            [TextureOverrideComponent2]
            drawindexed = 1000, 2000, 0

            [TextureOverrideComponent3]
            drawindexed = 1000, 3000, 0

            [TextureOverrideComponent4]
            drawindexed = 100000, 4000, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.Null(result.Body);
        Assert.Contains("头部", result.Reason);
    }

    [Fact]
    public void SkipsHairOnlyModFolderByName()
    {
        var mod = Path.Combine(_root, "尤诺-C酱发型 by 布鲁加尔");
        Directory.CreateDirectory(mod);
        File.WriteAllText(Path.Combine(mod, "mod.ini"), """
            namespace = TestMod

            [TextureOverrideComponent0]
            drawindexed = 900, 0, 0

            [TextureOverrideComponent1]
            drawindexed = 500, 900, 0
            """, new UTF8Encoding(false));

        var result = OilGlossScanner.Scan(mod);

        Assert.Null(result.Body);
        Assert.Contains("发型", result.Reason);
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