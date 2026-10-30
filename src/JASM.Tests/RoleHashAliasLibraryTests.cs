using GIMI_ModManager.Core.Services.WuwaUhd;

namespace JASM.Tests;

public class RoleHashAliasLibraryTests
{
    [Fact]
    public void ParsesHdToUhdMappingsAndSkipsInvalidRows()
    {
        var path = WriteTemp(
            "# RoleHash aliases v1\n" +
            "琳奈\t7e400733\t2\t0\t17970\t0\ta\tb\t0\t0\t0\tps\t1\tdd1f9bc8\t3\t2048\t2048\t1\t12\t1\t97\t1998df83\tdd1f9bc8\tcab2af64\t1\n" +
            "琳奈\t7e400733\t2\t0\t17970\t0\ta\tb\t0\t0\t0\tps\t5\t42dadfb2\t3\t2048\t2048\t1\t1\t1\t97\t48947f0c\t6b454451\tx\t2\n" +
            "琳奈\tbad\trow\n" +
            "other\t7e400733\t2\t0\t17970\t0\ta\tb\t0\t0\t0\tps\t1\t00000000\t3\t1\t1\t1\t1\t1\t1\t11111111\t00000000\tx\t1\n");

        try
        {
            var library = RoleHashAliasLibrary.Load(path);
            var lynae = RoleHashAliasLibrary.GetForRole(library, "琳奈");

            Assert.Equal(2, lynae.Count);
            Assert.Equal(["dd1f9bc8"], lynae["1998df83"]);
            Assert.Equal(["42dadfb2", "6b454451"], lynae["48947f0c"].OrderBy(x => x));
            Assert.DoesNotContain("other", library.Keys);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReturnsEmptyWhenTheFileIsMissing()
    {
        var library = RoleHashAliasLibrary.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".tsv"));

        Assert.Empty(library);
        Assert.Empty(RoleHashAliasLibrary.GetForRole(library, "琳奈"));
    }

    private static string WriteTemp(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "rolemap-" + Guid.NewGuid().ToString("N") + ".tsv");
        File.WriteAllText(path, text);
        return path;
    }
}
