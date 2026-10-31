using GIMI_ModManager.Core.Services.WuwaUhd;

namespace JASM.Tests;

public class UhdCoverageScannerTests
{
    [Fact]
    public void ParsesHashBindingsAndSkipsGeneratedSections()
    {
        const string ini = """
                           [ResourceDiffuse]
                           filename = Textures/foo.dds

                           [TextureOverrideTexture4];; comp 3
                           hash = 179ec8b9
                           this = ResourceDiffuse

                           [TextureOverrideTexture4_UHD_e2adee4a]
                           hash = e2adee4a
                           this = ResourceDiffuse

                           [TextureOverrideComponent0]
                           hash = 7e400733
                           """;

        var bindings = UhdCoverageScanner.ParseTextureBindings(ini).ToArray();

        var binding = Assert.Single(bindings);
        Assert.Equal("TextureOverrideTexture4", binding.Name);
        Assert.Equal("179ec8b9", binding.Hash);
    }

    [Fact]
    public void ScansModDirectoriesAndComputesCoverage()
    {
        var root = Path.Combine(Path.GetTempPath(), "coverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            File.WriteAllText(Path.Combine(root, "mod.ini"), """
                [TextureOverrideTexture0]
                hash = aaaaaaaa
                this = ResourceA

                [TextureOverrideTexture1]
                hash = bbbbbbbb
                this = ResourceB
                """);
            File.WriteAllText(Path.Combine(root, "mod.ini.BEFORE_x_uhd.bak"), "[TextureOverrideTexture9]\nhash = cccccccc");

            var mappings = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["aaaaaaaa"] = ["11111111"]
            };

            var character = UhdCoverageScanner.ScanCharacter("琳奈", "lynae",
                [("Mod", root)], mappings);

            var mod = Assert.Single(character.Mods);
            Assert.Equal(2, mod.TextureSections);
            Assert.Equal(2, mod.TotalHashes);
            Assert.Equal(1, mod.CoveredHashes);
            Assert.True(mod.NeedsLearning);
            Assert.True(character.NeedsLearning);
            Assert.Equal(1, character.UncoveredMods);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SlotBasedModsDoNotNeedLearning()
    {
        var root = Path.Combine(Path.GetTempPath(), "coverage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            File.WriteAllText(Path.Combine(root, "mod.ini"), """
                [CommandListTexture]
                ps-t5 = ref ResourceTexture0_5
                """);

            var character = UhdCoverageScanner.ScanCharacter("爱弥斯", "aemeath",
                [("Mod", root)], new Dictionary<string, IReadOnlyCollection<string>>());

            Assert.False(character.NeedsLearning);
            Assert.Equal(0, character.HashModCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReportListsCharactersNeedingLearning()
    {
        var character = new UhdCharacterCoverage("琳奈", "lynae",
        [
            new UhdModCoverage("琳奈", "Mod", @"C:\mods\a", 2, 1, 3),
            new UhdModCoverage("琳奈", "Done", @"C:\mods\b", 1, 1, 1)
        ]);

        var report = UhdCoverageScanner.FormatReport([character], DateTimeOffset.UnixEpoch);

        Assert.Contains("需要学习: 1", report);
        Assert.Contains("琳奈", report);
        Assert.Contains("覆盖 1/3", report);
    }
}