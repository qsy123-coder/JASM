using System.Text;
using GIMI_ModManager.Core.Services.WuwaUhd;

namespace JASM.Tests;

public class RocketBuiltinAliasExtractorTests
{
    [Fact]
    public void ExtractsEmbeddedAliasRows()
    {
        var blob = Blob(
            Row("琳奈", "dd1f9bc8", "1998df83"),
            Row("琳奈", "42dadfb2", "48947f0c"),
            Row("达妮娅", "7bcbb549", "e4025b5f"));

        var aliases = RocketBuiltinAliasExtractor.Extract(blob);

        Assert.Equal(2, aliases.Count);
        var lynae = Assert.Single(aliases, a => a.Role == "琳奈");
        Assert.Equal(2, lynae.Pairs.Count);
        Assert.Contains(("1998df83", "dd1f9bc8"), lynae.Pairs);
        Assert.Contains(("48947f0c", "42dadfb2"), lynae.Pairs);
        Assert.Contains(aliases, a => a.Role == "达妮娅");
    }

    [Fact]
    public void StopsAtFirstMalformedLine()
    {
        var blob = Blob(
            Row("琳奈", "dd1f9bc8", "1998df83"),
            "not\ta\tvalid\trow",
            Row("达妮娅", "7bcbb549", "e4025b5f"));

        var aliases = RocketBuiltinAliasExtractor.Extract(blob);

        var lynae = Assert.Single(aliases);
        Assert.Equal("琳奈", lynae.Role);
        Assert.Single(lynae.Pairs);
    }

    [Fact]
    public void RejectsAllZeroHashes()
    {
        var blob = Blob(Row("琳奈", "00000000", "1998df83"));

        Assert.Empty(RocketBuiltinAliasExtractor.Extract(blob));
    }

    [Fact]
    public void MergesExtractedPairsIntoLearningLibrary()
    {
        var blob = Blob(Row("达妮娅", "7bcbb549", "e4025b5f"));
        var libraryPath = Path.Combine(Path.GetTempPath(), "builtin-" + Guid.NewGuid().ToString("N") + ".tsv");

        try
        {
            var added = RocketBuiltinAliasExtractor.MergeIntoLibrary(
                RocketBuiltinAliasExtractor.Extract(blob), libraryPath);

            Assert.Equal(1, added);
            var mapping = UhdLearningStore.LoadMappingsForRole("达妮娅", rocketAliasesPath: "missing",
                learnedPath: libraryPath);
            Assert.Equal(["7bcbb549"], mapping["e4025b5f"]);
        }
        finally
        {
            if (File.Exists(libraryPath))
                File.Delete(libraryPath);
        }
    }

    private static byte[] Blob(params string[] rows)
    {
        var text = new StringBuilder();
        text.Append("# RoleHash aliases v1\n");
        foreach (var row in rows)
            text.Append(row).Append('\n');
        return Encoding.UTF8.GetBytes(text.ToString());
    }

    private static string Row(string role, string uhd, string hd)
    {
        var columns = new string[25];
        for (var i = 0; i < columns.Length; i++)
            columns[i] = "0";

        columns[0] = role;
        columns[1] = "7e400733";
        columns[4] = "17970";
        columns[11] = "ps";
        columns[12] = "3";
        columns[13] = uhd;
        columns[21] = hd;
        return string.Join('\t', columns);
    }
}