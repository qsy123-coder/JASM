using GIMI_ModManager.Core.Services.WuwaUhd;

namespace JASM.Tests;

public class UhdLearningStoreTests
{
    [Fact]
    public void ParsesOnlyValidObservationRows()
    {
        var path = WriteTemp("learn.tsv",
            "# JASM learn v1\n" +
            "# ib_hash\tfirst_index\tslot\ttexture_hash\tcount\n" +
            "7e400733\t17970\t3\t42dadfb2\t12\n" +
            "7e400733\t17970\t1\t4d8edb5e\t3\n" +
            "bad\trow\n" +
            "zzzzzzzz\t1\t2\taabbccdd\t1\n");

        try
        {
            var observations = UhdLearningStore.ParseObservations(path);

            Assert.Equal(2, observations.Count);
            Assert.Equal("7e400733", observations[0].IbHash);
            Assert.Equal(17970u, observations[0].FirstIndex);
            Assert.Equal((ushort)3, observations[0].Slot);
            Assert.Equal("42dadfb2", observations[0].TextureHash);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void JoinsHdAndUhdByDrawIdentity()
    {
        var hd = new List<UhdLearningStore.Observation>
        {
            new("7e400733", 17970, 3, "1998df83"),
            new("7e400733", 17970, 5, "48947f0c"),
            new("7e400733", 45636, 1, "265d844d")
        };
        var uhd = new List<UhdLearningStore.Observation>
        {
            new("7e400733", 17970, 3, "dd1f9bc8"),
            new("7e400733", 17970, 5, "42dadfb2"),
            new("7e400733", 45636, 1, "3f7c7bbd"),
            // 只在 UHD 出现的位置不参与配对
            new("7e400733", 45636, 2, "deadbeef")
        };

        var pairs = UhdLearningStore.Join(hd, uhd);

        Assert.Equal(3, pairs.Count);
        Assert.Contains(("1998df83", "dd1f9bc8"), pairs);
        Assert.Contains(("48947f0c", "42dadfb2"), pairs);
        Assert.Contains(("265d844d", "3f7c7bbd"), pairs);
    }

    [Fact]
    public void JoinSkipsIdenticalHashesAndDuplicates()
    {
        var observations = new List<UhdLearningStore.Observation>
        {
            new("7e400733", 1, 0, "aabbccdd"),
            new("7e400733", 1, 0, "aabbccdd")
        };

        Assert.Empty(UhdLearningStore.Join(observations, observations));
    }

    [Fact]
    public void MergeAndLoadRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "learned-" + Guid.NewGuid().ToString("N") + ".tsv");

        try
        {
            var added = UhdLearningStore.Merge("琳奈",
                [("48947f0c", "42dadfb2"), ("48947f0c", "6b454451"), ("7d044e58", "4d8edb5e")], path);
            Assert.Equal(3, added);

            // 重复合并不新增
            Assert.Equal(0, UhdLearningStore.Merge("琳奈", [("48947f0c", "42dadfb2")], path));

            var library = UhdLearningStore.Load(path);
            Assert.True(library.ContainsKey("琳奈"));
            Assert.Equal(["42dadfb2", "6b454451"], library["琳奈"]["48947f0c"].OrderBy(x => x));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadMappingsForRoleMergesRocketAndLearned()
    {
        var rocketPath = WriteTemp("rocket.tsv",
            "# RoleHash aliases v1\n" +
            "琳奈\t7e400733\t2\t0\t17970\t0\ta\tb\t0\t0\t0\tps\t5\t42dadfb2\t3\t2048\t2048\t1\t1\t1\t97\t48947f0c\t6b454451\tx\t2\n");
        var learnedPath = Path.Combine(Path.GetTempPath(), "learned-" + Guid.NewGuid().ToString("N") + ".tsv");

        try
        {
            UhdLearningStore.Merge("琳奈", [("7d044e58", "4d8edb5e")], learnedPath);

            var mappings = UhdLearningStore.LoadMappingsForRole("琳奈", rocketPath, learnedPath);

            Assert.Equal(["42dadfb2"], mappings["48947f0c"]);
            Assert.Equal(["4d8edb5e"], mappings["7d044e58"]);
        }
        finally
        {
            File.Delete(rocketPath);
            if (File.Exists(learnedPath))
                File.Delete(learnedPath);
        }
    }

    private static string WriteTemp(string name, string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "uhdlearn-" + Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllText(path, text);
        return path;
    }
}