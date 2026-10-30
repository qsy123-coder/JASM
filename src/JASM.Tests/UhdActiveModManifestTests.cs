using GIMI_ModManager.Core.Services.WuwaUhd;

namespace JASM.Tests;

public class UhdActiveModManifestTests
{
    private const string ModRoot = @"C:\Mods\Linnai";

    private const string SampleIni = """
                                     [ResourceDiffuse]
                                     filename = Texture\Diffuse.dds

                                     [TextureOverrideTexture0]
                                     hash = 1998df83
                                     match_priority = 0
                                     if $object_detected
                                         this = ResourceDiffuse
                                     endif

                                     [TextureOverrideComponent0]
                                     hash = 7e400733
                                     match_first_index = 0
                                     """;

    private static UhdActiveModManifest Build(string ini, string role = "琳奈",
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? aliases = null)
    {
        var input = new UhdActiveModInput("Linnai", ModRoot, [new UhdIniSource(Path.Combine(ModRoot, "mod.ini"), ini)]);
        return UhdActiveModManifestBuilder.Build(role, "linnai", [input], aliases,
            new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void BuildsOneEntryPerHashedTextureBinding()
    {
        var manifest = Build(SampleIni);

        var mod = Assert.Single(manifest.Mods);
        var texture = Assert.Single(mod.Textures);

        Assert.Equal("1998df83", texture.HdHash);
        Assert.Equal("ResourceDiffuse", texture.ResourceSection);
        Assert.Equal("TextureOverrideTexture0", texture.OverrideSection);
        Assert.Equal(Path.GetFullPath(Path.Combine(ModRoot, @"Texture\Diffuse.dds")), texture.DdsPath);
        Assert.Empty(texture.UhdHashes);
    }

    [Fact]
    public void SkipsComponentSectionsThatDoNotBindAResource()
    {
        var manifest = Build(SampleIni);

        Assert.DoesNotContain(manifest.Mods[0].Textures, t => t.HdHash == "7e400733");
    }

    [Fact]
    public void MergesGeneratedUhdSectionsIntoTheBaseEntry()
    {
        const string ini = """
                           [ResourceDiffuse]
                           filename = Texture\Diffuse.dds

                           [TextureOverrideTexture0]
                           hash = 1998df83
                           this = ResourceDiffuse

                           [TextureOverrideTexture0_UHD_dd1f9bc8]
                           hash = dd1f9bc8
                           this = ResourceDiffuse
                           """;

        var manifest = Build(ini);

        var texture = Assert.Single(manifest.Mods[0].Textures);
        Assert.Equal("1998df83", texture.HdHash);
        Assert.Equal(["dd1f9bc8"], texture.UhdHashes);
    }

    [Fact]
    public void MergesRocketAliasPairs()
    {
        var aliases = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["1998df83"] = ["dd1f9bc8", "4d8edb5e"]
        };

        var texture = Assert.Single(Build(SampleIni, aliases: aliases).Mods[0].Textures);

        Assert.Equal(["dd1f9bc8", "4d8edb5e"], texture.UhdHashes);
    }

    [Fact]
    public void EmitsOneEntryPerConditionalResourceBranch()
    {
        const string ini = """
                           [ResourceDiffuse]
                           filename = Texture\Diffuse.dds

                           [ResourceLightMap]
                           filename = Texture\LightMap.dds

                           [TextureOverrideTexture0]
                           hash = 1998df83
                           if $object_detected
                               this = ResourceDiffuse
                           else
                               this = ResourceLightMap
                           endif
                           """;

        var textures = Build(ini).Mods[0].Textures;

        Assert.Equal(2, textures.Count);
        Assert.Contains(textures, t => t.ResourceSection == "ResourceDiffuse");
        Assert.Contains(textures, t => t.ResourceSection == "ResourceLightMap");
    }

    [Fact]
    public void ResolvesQuotedAndForwardSlashFilenames()
    {
        const string ini = """
                           [ResourceDiffuse]
                           filename = "Texture/Diffuse.dds"

                           [TextureOverrideTexture0]
                           hash = 1998df83
                           this = ResourceDiffuse
                           """;

        var texture = Assert.Single(Build(ini).Mods[0].Textures);

        Assert.Equal(Path.GetFullPath(Path.Combine(ModRoot, @"Texture\Diffuse.dds")), texture.DdsPath);
    }

    [Fact]
    public void LeavesDdsPathNullWhenTheResourceHasNoFilename()
    {
        const string ini = """
                           [ResourceDiffuse]
                           type = Buffer

                           [TextureOverrideTexture0]
                           hash = 1998df83
                           this = ResourceDiffuse
                           """;

        var texture = Assert.Single(Build(ini).Mods[0].Textures);

        Assert.Null(texture.DdsPath);
    }

    [Fact]
    public void BuildFromDirectoryReadsTopLevelIniAndSkipsBackups()
    {
        var root = Path.Combine(Path.GetTempPath(), "jasmtests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            File.WriteAllText(Path.Combine(root, "mod.ini"), SampleIni);
            File.WriteAllText(Path.Combine(root, "mod.ini.BEFORE_20261030_uhd.bak"), SampleIni);
            File.WriteAllText(Path.Combine(root, "notes.txt"), "ignored");

            var manifest = UhdActiveModManifestBuilder.BuildFromDirectory(
                "琳奈", "linnai", [("Linnai", root)]);

            var mod = Assert.Single(manifest.Mods);
            Assert.Single(mod.IniFiles);
            Assert.EndsWith("mod.ini", mod.IniFiles[0], StringComparison.OrdinalIgnoreCase);
            Assert.Single(mod.Textures);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildFromDirectoryToleratesAMissingModFolder()
    {
        var manifest = UhdActiveModManifestBuilder.BuildFromDirectory(
            "琳奈", "linnai", [("Missing", Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid()))]);

        var mod = Assert.Single(manifest.Mods);
        Assert.Empty(mod.IniFiles);
        Assert.Empty(mod.Textures);
    }

    [Fact]
    public void SerializesWithTheDocumentedCamelCaseContract()
    {
        var json = UhdActiveModJson.Serialize(Build(SampleIni));

        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"generatedAtUtc\"", json);
        Assert.Contains("\"roleInternalName\": \"linnai\"", json);
        Assert.Contains("\"hdHash\": \"1998df83\"", json);
        Assert.Contains("\"overrideSection\": \"TextureOverrideTexture0\"", json);
        // 中文角色名保持可读，不转成 \uXXXX
        Assert.Contains("琳奈", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        var original = Build(SampleIni);

        var restored = UhdActiveModJson.Deserialize(UhdActiveModJson.Serialize(original));

        Assert.NotNull(restored);
        Assert.Equal(original.Role, restored!.Role);
        Assert.Equal(original.RoleInternalName, restored.RoleInternalName);
        Assert.Equal(original.Mods[0].Textures[0].DdsPath, restored.Mods[0].Textures[0].DdsPath);
        Assert.Equal(original.Mods[0].Textures[0].UhdHashes,
            restored.Mods[0].Textures[0].UhdHashes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{\"schemaVersion\": \"not-a-number\"}")]
    public void DeserializeReturnsNullInsteadOfThrowing(string? json)
        => Assert.Null(UhdActiveModJson.Deserialize(json));
}