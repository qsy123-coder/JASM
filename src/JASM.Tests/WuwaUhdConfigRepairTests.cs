using GIMI_ModManager.Core.Services.WuwaUhd;

namespace JASM.Tests;

/// <summary>
/// Locks down the INI edit rules used by the UHD texture repair: only the target keys in the
/// target sections change; comments, other sections and unrelated keys survive byte-for-byte.
/// </summary>
public class WuwaUhdConfigRepairTests
{
    [Fact]
    public void CreatesTheSectionAndKeysWhenTheFileIsEmpty()
    {
        var result = WuwaUhdConfigRepair.Apply(string.Empty, WuwaUhdConfigRepair.EngineSettings);

        Assert.Contains("[/Script/Engine.RendererSettings]", result);
        Assert.Contains("r.RayTracing.LoadConfig=0", result);
        Assert.Contains("r.MaxAnisotropy=1", result);
        Assert.Contains("r.Streaming.HiddenPrimitiveScale=20", result);
        Assert.Contains("[ConsoleVariables]", result);
        Assert.Contains("r.Kuro.SkeletalMesh.LODDistanceScale=25", result);
        Assert.Contains("r.Streaming.FullyLoadUsedTextures=1", result);
        Assert.True(WuwaUhdConfigRepair.IsApplied(result, WuwaUhdConfigRepair.EngineSettings));
    }

    [Fact]
    public void ReplacesExistingValuesAndKeepsUnrelatedLines()
    {
        const string original = """
                                ; user comment
                                [/Script/Engine.RendererSettings]
                                r.MaxAnisotropy=8
                                r.SomeOtherKey=keep-me
                                ; trailing comment

                                [/Script/Engine.PhysicsSettings]
                                r.MaxAnisotropy=16
                                """;

        var result = WuwaUhdConfigRepair.Apply(original, WuwaUhdConfigRepair.EngineSettings);

        Assert.Contains("; user comment", result);
        Assert.Contains("r.SomeOtherKey=keep-me", result);
        Assert.Contains("; trailing comment", result);
        Assert.Contains("r.MaxAnisotropy=1", result);
        Assert.DoesNotContain("r.MaxAnisotropy=8", result);

        // 另一个段里的同名键不能被误改
        Assert.Contains("[/Script/Engine.PhysicsSettings]", result);
        Assert.Contains("r.MaxAnisotropy=16", result);
    }

    [Fact]
    public void InsertsMissingKeysInsideTheExistingSection()
    {
        const string original = """
                                [/Script/Engine.RendererSettings]
                                r.Existing=1
                                [/Script/Engine.Other]
                                a=b
                                """;

        var result = WuwaUhdConfigRepair.Apply(original, WuwaUhdConfigRepair.EngineSettings);

        var rendererStart = result.IndexOf("[/Script/Engine.RendererSettings]", StringComparison.Ordinal);
        var otherStart = result.IndexOf("[/Script/Engine.Other]", StringComparison.Ordinal);
        var anisotropy = result.IndexOf("r.MaxAnisotropy=1", StringComparison.Ordinal);

        Assert.True(rendererStart >= 0 && otherStart > rendererStart);
        Assert.InRange(anisotropy, rendererStart, otherStart);
        Assert.Contains("r.Existing=1", result);
    }

    [Fact]
    public void AddsTheConsoleVariablesSectionToAnExistingEngineIni()
    {
        const string original = """
                                [/Script/Engine.RendererSettings]
                                r.MaxAnisotropy=1
                                """;

        var result = WuwaUhdConfigRepair.Apply(original, WuwaUhdConfigRepair.EngineSettings);

        Assert.Contains("[ConsoleVariables]", result);
        Assert.Contains("r.Kuro.SkeletalMesh.LODDistanceScale=25", result);
        Assert.Contains("r.Streaming.FullyLoadUsedTextures=1", result);
        Assert.True(WuwaUhdConfigRepair.IsApplied(result, WuwaUhdConfigRepair.EngineSettings));
    }

    [Fact]
    public void IsIdempotent()
    {
        var once = WuwaUhdConfigRepair.Apply(string.Empty, WuwaUhdConfigRepair.GameUserSettingsSettings);
        var twice = WuwaUhdConfigRepair.Apply(once, WuwaUhdConfigRepair.GameUserSettingsSettings);

        Assert.Equal(once, twice);
    }

    [Fact]
    public void IsAppliedIsFalseWhenOneValueDiffers()
    {
        const string text = """
                            [/Script/Engine.RendererSettings]
                            r.RayTracing.LoadConfig=0
                            r.MaxAnisotropy=0
                            r.Streaming.HiddenPrimitiveScale=20
                            """;

        Assert.False(WuwaUhdConfigRepair.IsApplied(text, WuwaUhdConfigRepair.EngineSettings));
    }

    [Fact]
    public void PreservesCrLfLineEndings()
    {
        const string original = "[/Script/Engine.RendererSettings]\r\nr.MaxAnisotropy=8\r\n";

        var result = WuwaUhdConfigRepair.Apply(original, WuwaUhdConfigRepair.EngineSettings);

        Assert.Contains("\r\n", result);
        // 所有换行都必须是 CRLF；保留段之间的空行（新段前会插一个）
        Assert.DoesNotContain("\n", result.Replace("\r\n", string.Empty, StringComparison.Ordinal));
    }
}
