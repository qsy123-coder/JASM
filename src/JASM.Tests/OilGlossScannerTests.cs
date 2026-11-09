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
        // 默认隐藏的变体不参与身体锚点选择，但会进衣物组：
        // 用户在菜单里切到该变体后油光仍能生效。
        Assert.Equal(900, result.Cloth!.IndexCount);
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
    public void UsesMeshHeightToPickBodyAndExcludeHeadRegion()
    {
        var mod = Path.Combine(_root, "GeometryMod");
        var meshes = Path.Combine(mod, "Meshes");
        Directory.CreateDirectory(meshes);

        // WWMI 模型 Z 轴向上：0 = 脚底，100 = 头顶。
        // 0-2 脚底、3-5 躯干（身体）；6-11 头部；12-17 头发（从腰部到头顶）；
        // 18-23 上半身外套（与身体分开的另一块）。
        var heights = new[]
        {
            0f, 0f, 0f, 80f, 80f, 80f,
            95f, 96f, 97f, 98f, 99f, 100f,
            55f, 60f, 70f, 80f, 90f, 100f,
            10f, 30f, 50f, 70f, 85f, 90f,
        };

        using (var stream = File.Create(Path.Combine(meshes, "Position.buf")))
        using (var writer = new BinaryWriter(stream))
        {
            foreach (var z in heights)
            {
                writer.Write(0f);
                writer.Write(0f);
                writer.Write(z);
            }
        }

        // 24 个 32 位索引：四块网格各 6 个。
        var indexValues = new uint[]
        {
            0, 1, 2, 3, 4, 5,
            6, 7, 8, 9, 10, 11,
            12, 13, 14, 15, 16, 17,
            18, 19, 20, 21, 22, 23,
        };
        using (var stream = File.Create(Path.Combine(meshes, "Index.buf")))
        using (var writer = new BinaryWriter(stream))
        {
            foreach (var index in indexValues)
                writer.Write(index);
        }

        File.WriteAllText(Path.Combine(mod, "mod.ini"), """
            namespace = TestMod

            [TextureOverrideComponent0]
            drawindexed = 6, 0, 0

            [TextureOverrideComponent1]
            drawindexed = 6, 6, 0

            [TextureOverrideComponent2]
            drawindexed = 6, 12, 0

            [TextureOverrideComponent3]
            drawindexed = 6, 18, 0
            """, new UTF8Encoding(false));

        var result = OilGlossScanner.Scan(mod);

        // 身体：跨下半身到躯干，按高度选它而不是按索引数猜。
        Assert.Equal(0, result.Body!.StartIndex);
        Assert.Equal(OilGlossGroup.Body, result.Drawcalls.Single(d => d.StartIndex == 0).Group);

        // 上半身外套不自动并进身体组：包到不支持的材质上会贴图错误，
        // 需要时用「逐块识别身体」手动指定。
        Assert.Equal(OilGlossGroup.Cloth, result.Drawcalls.Single(d => d.StartIndex == 18).Group);

        // 头部整块都在 78% 以上 → 不包油光。
        var head = result.Drawcalls.Single(d => d.StartIndex == 6);
        Assert.True(head.IsHeadLike);
        Assert.Null(head.Group);

        // 头发从腰部伸到头顶：不能分进衣物组，否则开衣物油光就整头变绿。
        var hair = result.Drawcalls.Single(d => d.StartIndex == 12);
        Assert.True(hair.IsHeadLike);
        Assert.Null(hair.Group);
    }

    [Fact]
    public void ScansModsWhoseFolderNameMentionsHair()
    {
        // 「发型」只是 Mod 的名字；不少整模 Mod（例如尤诺-C酱发型）其实带完整身体。
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

        Assert.NotNull(result.Body);
        Assert.Equal(900, result.Body!.IndexCount);
    }

    [Fact]
    public void EvaluatesComparisonConditionsInsteadOfVariableTruthiness()
    {
        // if $choker == 0 表示「变量为 0 时显示」。只拿 $choker 判真假（0 = 假）
        // 会把这块从可见集合里排除，进而把真正的身体网格漏掉。
        var mod = CreateMod("""
            namespace = TestMod

            [Constants]
            global persist $choker = 0
            global persist $swapvar_toggle_0 = 0
            global $draw_component_3_005 = 1

            [CommandListProcessToggles]
            $draw_component_3_005 = ($swapvar_toggle_0 == 1)

            [TextureOverrideComponent3]
            if $draw_component_3_005
            drawindexed = 5000, 0, 0
            endif
            if $choker == 0
            drawindexed = 900, 5000, 0
            endif
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.True(result.Drawcalls.Single(d => d.IndexCount == 900).VisibleByDefault);
        Assert.False(result.Drawcalls.Single(d => d.IndexCount == 5000).VisibleByDefault);
        Assert.Equal(900, result.Body!.IndexCount);
    }

    [Fact]
    public void IgnoresConditionsFromClosedBranches()
    {
        // 身体块紧跟在已结束的 $headacc 分支后面，不能被误判成头部。
        var mod = CreateMod("""
            namespace = TestMod

            [Constants]
            global persist $headacc = 0

            [TextureOverrideComponent4]
            if $headacc == 0
            drawindexed = 200, 0, 0
            endif
            drawindexed = 1000, 200, 0
            """);

        var result = OilGlossScanner.Scan(mod);

        Assert.True(result.Drawcalls.Single(d => d.IndexCount == 200).IsHeadLike);
        Assert.False(result.Drawcalls.Single(d => d.IndexCount == 1000).IsHeadLike);
        Assert.Equal(1000, result.Body!.IndexCount);
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